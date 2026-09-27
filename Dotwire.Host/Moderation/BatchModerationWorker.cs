using System.Diagnostics;

namespace Dotwire.Host.Moderation;

public sealed class BatchModerationOptions
{
    /// <summary>Flush when this many messages are queued...</summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>...or when the oldest queued message has waited this long, whichever comes first.</summary>
    public TimeSpan MaxBatchDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Classifier attempts per batch (exponential backoff between them) before the batch is dropped.</summary>
    public int MaxClassifierAttempts { get; init; } = 3;
}

/// <summary>
/// Drains a <see cref="ModerationQueue"/> in size-or-time bounded batches, hands each batch to
/// the <see cref="IModerationClassifier"/>, and runs <c>onFlagged</c> for every flagged verdict
/// (typically <see cref="RedactWith"/>). Runs entirely off the send path: the only cost a send
/// pays for moderation is the channel write in <see cref="ModerationQueue.PostSendHook"/>.
/// Host it however you host background work - a <c>BackgroundService</c> calling
/// <see cref="RunAsync"/> is the usual shape.
/// </summary>
public sealed class BatchModerationWorker(
    ModerationQueue queue,
    IModerationClassifier classifier,
    Func<ModerationVerdict, CancellationToken, Task> onFlagged,
    BatchModerationOptions? options = null,
    Action<Exception>? onError = null)
{
    private readonly BatchModerationOptions _options = options ?? new BatchModerationOptions();

    /// <summary>
    /// The usual action: physically redact the flagged message through the admin API. Retries
    /// up to <paramref name="maxAttempts"/> times on a 429, honoring the server's
    /// <c>Retry-After</c> (<see cref="DotwireApiException.RetryAfter"/>) between attempts.
    /// </summary>
    public static Func<ModerationVerdict, CancellationToken, Task> RedactWith(IDotwireHostClient client, int maxAttempts = 3) =>
        async (verdict, ct) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await client.RedactMessageAsync(verdict.RoomId, verdict.Seq, ct);
                    return;
                }
                catch (DotwireApiException ex) when (ex.Kind == DotwireErrorKind.RateLimited && attempt < maxAttempts)
                {
                    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(1), ct);
                }
            }
        };

    /// <summary>Runs until cancelled or until the queue is completed and drained.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<PendingModeration>(_options.MaxBatchSize);
        try
        {
            while (await FillBatchAsync(batch, ct))
            {
                await ProcessBatchAsync(batch, ct);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Blocks for the first item, then lingers up to MaxBatchDelay collecting more. False when the queue is done.</summary>
    private async Task<bool> FillBatchAsync(List<PendingModeration> batch, CancellationToken ct)
    {
        var reader = queue.Reader;
        if (!await reader.WaitToReadAsync(ct))
            return false;

        var started = Stopwatch.GetTimestamp();
        while (batch.Count < _options.MaxBatchSize)
        {
            while (batch.Count < _options.MaxBatchSize && reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count >= _options.MaxBatchSize)
                break;

            var remaining = _options.MaxBatchDelay - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                break;

            using var linger = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linger.CancelAfter(remaining);
            try
            {
                if (!await reader.WaitToReadAsync(linger.Token))
                    break; // completed: flush what we have
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break; // linger elapsed
            }
        }

        return batch.Count > 0;
    }

    private async Task ProcessBatchAsync(List<PendingModeration> batch, CancellationToken ct)
    {
        IReadOnlyList<ModerationVerdict>? verdicts = null;
        var input = batch.ToArray();
        for (var attempt = 1; attempt <= _options.MaxClassifierAttempts; attempt++)
        {
            try
            {
                verdicts = await classifier.ClassifyAsync(input, ct);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                if (attempt == _options.MaxClassifierAttempts)
                    return; // best-effort second line of defense: drop the batch, don't wedge the worker
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        if (verdicts is null)
            return;

        foreach (var verdict in verdicts)
        {
            if (!verdict.Flagged)
                continue;

            try
            {
                await onFlagged(verdict, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }
    }
}
