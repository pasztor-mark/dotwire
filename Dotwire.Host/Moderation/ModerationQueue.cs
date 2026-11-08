using System.Threading.Channels;

namespace Dotwire.Host.Moderation;

/// <summary>
/// In-process hand-off between the send path and the <see cref="BatchModerationWorker"/>.
/// Enqueueing is a channel write - microseconds - so wiring <see cref="PostSendHook"/> into
/// <see cref="DotwireHostClient.PostSend"/> adds no meaningful latency to a send. The verdict
/// itself is computed later, in batches, by the worker.
/// </summary>
/// <param name="capacity">Bound on messages awaiting review.</param>
/// <param name="fullMode">
/// What happens when the worker falls <paramref name="capacity"/> messages behind. The default,
/// <see cref="BoundedChannelFullMode.Wait"/>, applies back-pressure to sends rather than silently
/// skipping review; pick <see cref="BoundedChannelFullMode.DropWrite"/> if send latency must win.
/// </param>
public sealed class ModerationQueue(
    int capacity = 10_000,
    BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
    TimeSpan? enqueueTimeout = null)
{
    private readonly TimeSpan _enqueueTimeout = enqueueTimeout ?? TimeSpan.FromSeconds(30);

    private readonly Channel<PendingModeration> _channel = Channel.CreateBounded<PendingModeration>(
        new BoundedChannelOptions(capacity) { FullMode = fullMode, SingleReader = true });

    public ChannelReader<PendingModeration> Reader => _channel.Reader;

    public async ValueTask EnqueueAsync(PendingModeration item, CancellationToken ct = default)
    {
        if (_channel.Writer.TryWrite(item))
            return;

        using var timeout = new CancellationTokenSource(_enqueueTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await _channel.Writer.WriteAsync(item, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Moderation queue stayed full for {_enqueueTimeout}; the message was sent but not queued for review.");
        }
    }

    /// <summary>Assign directly: <c>client.PostSend = queue.PostSendHook;</c></summary>
    public Task PostSendHook(PostSendContext context, CancellationToken ct) =>
        EnqueueAsync(new PendingModeration(context.RoomId, context.Seq, context.Time, context.SenderUserId, context.Content), ct).AsTask();

    /// <summary>Signals the worker to drain what is queued and stop.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
