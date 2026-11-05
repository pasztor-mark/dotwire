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
            IOptions<SseOptions> sseOptionsAccessor,
            ILoggerFactory loggerFactory) =>
        {
            var sseOptions = sseOptionsAccessor.Value;
            if (!sseOptions.Enabled)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var roomGuid = Guid.Parse(roomId);
            var userId = http.User.FindFirstValue("sub")!;
            var logger = loggerFactory.CreateLogger("Dotwire.Api.Events");

            ulong? resumeSeq = null;
            if (http.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId) &&
                ulong.TryParse(lastEventId, out var parsedLast))
            {
                resumeSeq = parsedLast;
            }
            else if (afterSeq.HasValue)
            {
                resumeSeq = afterSeq;
            }

            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Response.ContentType = "text/event-stream";
            await http.Response.StartAsync(http.RequestAborted);

            var connectionId = $"sse-{Guid.NewGuid()}";
            var lastReplayedSeq = resumeSeq ?? 0;
            var revoked = CancellationToken.None;
            try
            {
                // Register the live subscription FIRST (spec §3.9) so live events start
                // accumulating in this connection's channel while replay runs below.
                (var reader, revoked) = await interestManager.RegisterSseConnectionAsync(roomGuid, connectionId, userId, sseOptions.BufferSize);
                presenceCoalescer.QueueJoin(roomGuid, userId);

                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, revoked);
                if (long.TryParse(http.User.FindFirstValue("exp"), out var exp))
                {
                    var remaining = DateTimeOffset.FromUnixTimeSeconds(exp) - DateTimeOffset.UtcNow;
                    lifetime.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 0, int.MaxValue)));
                }
                var ct = lifetime.Token;

                if (resumeSeq.HasValue)
                {
                    var current = resumeSeq;
                    while (true)
                    {
                        var (rows, hasMore) = await Messages.QueryHistoryPageAsync(
                            readDataSource, js, natsOptions.Value, logger, roomGuid, current, null, sseOptions.ReplayPageSize, ct);

                        foreach (var row in rows)
                        {
                            try
                            {
                                var decrypted = cipher.Decrypt(row.KeyId, row.Content);
                                var text = Encoding.UTF8.GetString(decrypted);
                                var payload = new SseMessagePayload(roomGuid, row.Seq, row.SenderId, row.Time, text, Replayed: true);
                                var data = JsonSerializer.SerializeToUtf8Bytes(payload, DotwireJsonContext.Default.SseMessagePayload);
                                await WriteEventAsync(http.Response, "message", data, row.Seq, ct);
                                lastReplayedSeq = row.Seq;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogError("Failed to decrypt message {Seq} during SSE replay for room {RoomId}: {Error}",
                                    row.Seq, roomGuid, ex.Message);
                            }
                        }

                        if (!hasMore || rows.Count == 0)
                            break;
                        current = lastReplayedSeq;
                    }
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
                if (revoked.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
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
                else if (ex is not OperationCanceledException)
                {
                    logger.LogDebug("SSE connection for room {RoomId} ended: {Error}", roomGuid, ex.Message);
                }
            }
            finally
            {
                interestManager.UnregisterSseConnection(roomGuid, connectionId);
                presenceCoalescer.QueueLeave(roomGuid, userId);
            }
        });
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
