using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Realtime;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Npgsql;

namespace Dotwire.Api;

/// <summary>SSE read path (spec §3.9): `GET /rooms/{roomId}/events`. Runs on the Gateway node role.</summary>
public static class Events
{
    public static void MapEventEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/rooms/{roomId}/events")
            .RequireAuthorization()
            .AddEndpointFilter<RoleCrossCheckFilter>()
            .AddEndpointFilter<RoomMembershipFilter>()
            .RequireRateLimiting("read");

        group.MapGet("/", async (
            string roomId,
            ulong? afterSeq,
            HttpContext http,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            MessageCipher cipher,
            INatsJSContext js,
            IOptions<NatsOptions> natsOptions,
            RoomInterestManager interestManager,
            Dotwire.Realtime.PresenceCoalescer presenceCoalescer,
            IAccessStore accessStore,
            UserConnectionLimiter connectionLimiter,
            IOptions<SseOptions> sseOptionsAccessor,
            ILoggerFactory loggerFactory) =>
        {
            var sseOptions = sseOptionsAccessor.Value;
            if (!sseOptions.Enabled || !Guid.TryParse(roomId, out var roomGuid))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var userId = http.User.FindFirstValue("sub")!;
            var role = http.User.FindFirstValue("dw:role");
            var logger = loggerFactory.CreateLogger("Dotwire.Api.Events");

            http.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId);
            var resumeSeq = ResolveResumeSeq(lastEventId, afterSeq);

            if (!connectionLimiter.TryAcquire(userId))
            {
                await ApiResults.RateLimited(1).ExecuteAsync(http);
                return;
            }

            var connectionId = $"sse-{Guid.NewGuid()}";
            var lastReplayedSeq = resumeSeq ?? 0;
            var revoked = CancellationToken.None;
            var joined = false;
            try
            {
                (var reader, revoked) = await interestManager.RegisterSseConnectionAsync(roomGuid, connectionId, userId, sseOptions.BufferSize, role);

                if (!await accessStore.IsMemberAsync(roomGuid, userId, http.RequestAborted))
                {
                    interestManager.UnregisterSseConnection(roomGuid, connectionId);
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                presenceCoalescer.QueueJoin(roomGuid, userId);
                joined = true;

                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";
                http.Response.ContentType = "text/event-stream";
                await http.Response.StartAsync(http.RequestAborted);

                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, revoked);
                if (long.TryParse(http.User.FindFirstValue("exp"), out var exp))
                {
                    var remaining = DateTimeOffset.FromUnixTimeSeconds(exp) - DateTimeOffset.UtcNow;
                    lifetime.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 0, int.MaxValue)));
                }
                var ct = lifetime.Token;

                if (resumeSeq.HasValue)
                {
                    var (replayedTo, truncated) = await ReplayAsync(
                        resumeSeq.Value,
                        sseOptions.MaxReplayRows,
                        (current, token) => Messages.QueryHistoryPageAsync(
                            readDataSource, js, natsOptions.Value, logger, roomGuid, current, null, sseOptions.ReplayPageSize, token),
                        async (row, token) =>
                        {
                            byte[] decrypted;
                            try
                            {
                                decrypted = cipher.Decrypt(row.KeyId, row.Content, roomGuid, row.SenderId);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogError("Failed to decrypt message {Seq} during SSE replay for room {RoomId}: {Error}",
                                    row.Seq, roomGuid, ex.Message);
                                return false;
                            }

                            var payload = new SseMessagePayload(roomGuid, row.Seq, row.SenderId, row.Time, Encoding.UTF8.GetString(decrypted), Replayed: true);
                            var data = JsonSerializer.SerializeToUtf8Bytes(payload, DotwireJsonContext.Default.SseMessagePayload);
                            await WriteEventAsync(http.Response, "message", data, row.Seq, token);
                            return true;
                        },
                        ct);

                    lastReplayedSeq = replayedTo;
                    if (truncated)
                        return;
                }

                using var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(sseOptions.KeepAliveSeconds));

                // PeriodicTimer/ChannelReader each allow only ONE outstanding wait call at a
                // time - re-issuing WaitForNextTickAsync while a prior call is still pending
                // throws "Operation is not valid due to the current state of the object", so
                // each task is only replaced once IT completes, never the other's.
                var readTask = reader.WaitToReadAsync(ct).AsTask();
                var tickTask = keepAliveTimer.WaitForNextTickAsync(ct).AsTask();
                while (true)
                {
                    var completed = await Task.WhenAny(readTask, tickTask);

                    if (completed == tickTask)
                    {
                        if (!await tickTask)
                            break;
                        await http.Response.WriteAsync(": ping\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        tickTask = keepAliveTimer.WaitForNextTickAsync(ct).AsTask();
                        continue;
                    }

                    if (!await readTask)
                        break; // channel completed - laggard disconnect or normal teardown.

                    while (reader.TryRead(out var evt))
                    {
                        // Drain-and-dedupe: drop anything at or below the last replayed seq
                        // (already delivered above; this swallows live traffic that
                        // accumulated in the channel during replay).
                        if (evt.EventName == "message" && evt.Id.HasValue && evt.Id.Value <= lastReplayedSeq)
                            continue;

                        await WriteEventAsync(http.Response, evt.EventName, evt.Data, evt.Id, ct);
                    }

                    readTask = reader.WaitToReadAsync(ct).AsTask();
                }
            }
            catch (Exception ex)
            {
                // Response headers are already flushed by this point, so nothing can turn
                // into an error status any more - cancellation (client gone) and any
                // mid-stream I/O fault both just end the connection cleanly.
                if (revoked.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested && http.Response.HasStarted)
                {
                    try
                    {
                        var payload = JsonSerializer.SerializeToUtf8Bytes(new SseRevokedPayload(roomGuid), DotwireJsonContext.Default.SseRevokedPayload);
                        await WriteEventAsync(http.Response, "revoked", payload, null, http.RequestAborted);
                    }
                    catch (Exception writeEx)
                    {
                        logger.LogDebug("SSE revoked event for room {RoomId} not delivered: {Error}", roomGuid, writeEx.Message);
                    }
                }
                else if (!http.Response.HasStarted)
                {
                    throw;
                }
                else if (ex is not OperationCanceledException)
                {
                    logger.LogDebug("SSE connection for room {RoomId} ended: {Error}", roomGuid, ex.Message);
                }
            }
            finally
            {
                if (interestManager.UnregisterSseConnection(roomGuid, connectionId) && joined)
                    presenceCoalescer.QueueLeave(roomGuid, userId);
                connectionLimiter.Release(userId);
            }
        });
    }

    internal static ulong? ResolveResumeSeq(string? lastEventId, ulong? afterSeq) =>
        ulong.TryParse(lastEventId, out var parsed) ? parsed : afterSeq;

    internal static async Task<(ulong LastSeq, bool Truncated)> ReplayAsync(
        ulong fromSeq,
        int maxRows,
        Func<ulong, CancellationToken, Task<(List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)> Rows, bool HasMore)>> fetchPage,
        Func<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content), CancellationToken, Task<bool>> deliver,
        CancellationToken ct)
    {
        var current = fromSeq;
        var delivered = 0;
        while (true)
        {
            var (rows, hasMore) = await fetchPage(current, ct);
            for (var i = 0; i < rows.Count; i++)
            {
                if (await deliver(rows[i], ct))
                    delivered++;
                current = Math.Max(current, rows[i].Seq);

                if (maxRows > 0 && delivered >= maxRows && (i < rows.Count - 1 || hasMore))
                    return (current, true);
            }

            if (!hasMore || rows.Count == 0)
                return (current, false);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, byte[] data, ulong? id, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (id.HasValue)
            sb.Append("id: ").Append(id.Value).Append('\n');
        sb.Append("event: ").Append(eventName).Append('\n');
        sb.Append("data: ").Append(Encoding.UTF8.GetString(data)).Append('\n').Append('\n');
        await response.WriteAsync(sb.ToString(), ct);
        await response.Body.FlushAsync(ct);
    }
}
