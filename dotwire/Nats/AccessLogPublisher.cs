using System.Security.Claims;
using System.Threading.Channels;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Dotwire.Nats;

public static class AccessEvents
{
    public const string Opened = "live.opened";
    public const string Closed = "live.closed";
    public const string HistoryRead = "history.read";

    public const string Hub = "hub";
    public const string Sse = "sse";
    public const string Rest = "rest";

    public const string Left = "left";
    public const string Disconnected = "disconnected";
    public const string Revoked = "revoked";
    public const string Error = "error";

    public static AccessEvent? OpenEvent(ClaimsPrincipal user, Guid roomId, string via)
    {
        if (!long.TryParse(user.FindFirstValue("exp"), out var exp))
            return null;

        return new AccessEvent(
            Opened,
            DateTimeOffset.UtcNow,
            roomId,
            user.FindFirstValue("sub") ?? string.Empty,
            user.FindFirstValue("dw:actor"),
            user.FindFirstValue("dw:role") ?? string.Empty,
            via,
            Guid.NewGuid(),
            DateTimeOffset.FromUnixTimeSeconds(exp));
    }

    public static AccessEvent Close(AccessEvent opened, string reason, DateTimeOffset time) =>
        opened with { Event = Closed, Time = time, Reason = reason, ExpiresAt = null };
}

public sealed class AccessLogUnavailableException : Exception
{
    public AccessLogUnavailableException() : base("Access log is unavailable.")
    {
    }

    public AccessLogUnavailableException(Exception inner) : base("Access log is unavailable.", inner)
    {
    }
}

public class AccessLogPublisher(
    INatsJSContext js,
    IOptions<NatsOptions> nats,
    IOptions<AccessLogOptions> accessLog,
    DotwireMetrics metrics,
    ILogger<AccessLogPublisher> logger) : BackgroundService
{
    private const int CloseQueueCapacity = 10000;
    private const int CloseAttempts = 3;
    private static readonly TimeSpan CloseRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<AccessEvent> _closes = Channel.CreateBounded<AccessEvent>(
        new BoundedChannelOptions(CloseQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite },
        _ => metrics.AccessLogCloseDropped("queue_full"));

    public virtual bool IsEnabled => accessLog.Value.Enabled && nats.Value.Enabled;

    public virtual async Task RecordAsync(AccessEvent evt, CancellationToken ct)
    {
        if (!IsEnabled)
            return;

        var opened = evt.Event == AccessEvents.Opened;
        var msgId = opened ? $"{evt.WindowId}.opened" : Guid.NewGuid().ToString();
        try
        {
            if (!await TryPublishAsync(evt, msgId, ct))
                throw new InvalidOperationException("JetStream publish returned an error ack");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (opened)
                RecordClose(AccessEvents.Close(evt, AccessEvents.Error, DateTimeOffset.UtcNow));
            throw;
        }
        catch (Exception ex)
        {
            metrics.AccessLogUnavailable(opened ? "opened" : "read");
            if (opened)
                RecordClose(AccessEvents.Close(evt, AccessEvents.Error, DateTimeOffset.UtcNow));
            throw new AccessLogUnavailableException(ex);
        }
    }

    public virtual void RecordClose(AccessEvent evt)
    {
        if (!IsEnabled)
            return;
        if (!_closes.Writer.TryWrite(evt))
            metrics.AccessLogCloseDropped("publish_failed");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var evt in _closes.Reader.ReadAllAsync(stoppingToken))
                await PublishCloseOrCountAsync(evt, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        _closes.Writer.TryComplete();
        using var drain = new CancellationTokenSource(DrainTimeout);
        while (_closes.Reader.TryRead(out var evt))
            await PublishCloseOrCountAsync(evt, drain.Token);
    }

    private async Task PublishCloseOrCountAsync(AccessEvent evt, CancellationToken ct)
    {
        try
        {
            await PublishCloseAsync(evt, ct);
        }
        catch (OperationCanceledException)
        {
            metrics.AccessLogCloseDropped("publish_failed");
        }
    }

    private async Task PublishCloseAsync(AccessEvent evt, CancellationToken ct)
    {
        var msgId = $"{evt.WindowId}.closed";
        for (var attempt = 1; attempt <= CloseAttempts; attempt++)
        {
            try
            {
                if (await TryPublishAsync(evt, msgId, ct))
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
            }

            if (attempt < CloseAttempts)
                await Task.Delay(CloseRetryDelay, ct);
        }

        metrics.AccessLogCloseDropped("publish_failed");
        logger.LogWarning("Dropped access log event {Event} after {Attempts} failed publishes.", evt.Event, CloseAttempts);
    }

    private async Task<bool> TryPublishAsync(AccessEvent evt, string msgId, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, DotwireJsonContext.Default.AccessEvent);
        var headers = new NatsHeaders { ["Nats-Msg-Id"] = msgId };
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(PublishTimeout);
        try
        {
            var ack = await js.PublishAsync<byte[]>(nats.Value.AccessLogSubject, payload, headers: headers, cancellationToken: attempt.Token);
            return ack.Error is null || ack.Duplicate;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("JetStream publish timed out");
        }
        catch (NatsJSDuplicateMessageException)
        {
            return true;
        }
    }
}
