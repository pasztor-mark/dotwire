using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Npgsql;

namespace Dotwire.Api;

public sealed record SendMessageRequest(string? Content);

public sealed record SendMessageResponse(ulong Seq, DateTimeOffset Time);

public sealed record RoomMessage(Guid RoomId, string SenderId, DateTimeOffset Time, string KeyId, byte[] Content);

/// <summary>Core (non-JetStream), seq-tagged live fanout payload on <c>room.{roomId}.live</c> (spec §3.1).</summary>
public sealed record RoomLiveMessage(Guid RoomId, ulong Seq, string SenderId, DateTimeOffset Time, string KeyId, byte[] Content);

public sealed record RoomMessageDelivery(Guid RoomId, ulong Seq, string SenderId, DateTimeOffset Time, string Content);

public sealed record TypingNotification(Guid RoomId, string UserId, bool IsTyping);

public sealed record PresenceDelta(Guid RoomId, string[] Joined, string[] Left);

public sealed record HistoryMessage(ulong Seq, DateTimeOffset Time, string SenderId, string Content);

public sealed record RoomHistoryResponse(Guid RoomId, HistoryMessage[] Messages, bool HasMore);

/// <summary>Ephemeral (NATS core) notice that a message was redacted; clients drop it locally.</summary>
public sealed record MessageRetracted(Guid RoomId, ulong Seq);

/// <summary>Ephemeral (NATS core) notice that a user's room membership was revoked (spec §3.5).</summary>
public sealed record MembershipRevoked(Guid RoomId, string UserId);

/// <summary>Admin message injection request (spec §3.3). <c>SenderId</c> defaults to "system".</summary>
public sealed record InjectMessageRequest(string? Content, string? SenderId);

/// <summary>Audit events carry ids only, never content (ARCHITECTURE.md, "Audit log").</summary>
public sealed record AuditEvent(
    string EventType,
    Guid? RoomId,
    ulong? MessageSeq,
    string? ActorId,
    string? SubjectId,
    long? Value,
    DateTimeOffset Time);

public sealed record RedactMessageResponse(Guid RoomId, ulong Seq, bool RemovedFromHistory, bool RemovedFromStream);

/// <summary>Retention get/set response (spec §3.11). 0 = indefinite.</summary>
public sealed record RetentionResponse(int MessagesDays);

/// <summary>Retention set request (spec §3.11). 0 = indefinite, max 36500.</summary>
public sealed record SetRetentionRequest(int MessagesDays);

/// <summary>A durable, hash-chained audit_log row as returned by `/audit*` (spec §3.7). `Hash` is lowercase hex.</summary>
public sealed record AuditEntry(
    long Id,
    string EventType,
    Guid? RoomId,
    ulong? MessageSeq,
    string? ActorId,
    string? SubjectId,
    long? Value,
    DateTimeOffset Time,
    string Hash);

public sealed record AuditPageResponse(AuditEntry[] Events, bool HasMore);

public sealed record AuditVerificationResponse(bool Ok, long FromId, long ToId, long Checked, long? FirstInvalidId, DateOnly? Anchor);

public sealed record AuditCheckpointEntry(DateOnly Day, long LastId, string Hash, DateTimeOffset CreatedAt);

public sealed record AuditCheckpointsResponse(AuditCheckpointEntry[] Checkpoints);

/// <summary>New 4xx/5xx failure body shape (spec §3.14): `{ "error": "&lt;code&gt;", "reason"?: "&lt;text&gt;" }`.</summary>
public sealed record ApiErrorResponse(string Error, string? Reason = null);

/// <summary>Presend webhook request body (spec §3.8).</summary>
public sealed record PresendWebhookRequest(Guid RoomId, string SenderId, string Content, DateTimeOffset Time, string Source);

/// <summary>Presend webhook response body: `{allow:true, content?}` or `{allow:false, reason?}` (spec §3.8).</summary>
public sealed record PresendWebhookResponse(bool Allow, string? Content, string? Reason);

/// <summary>Postsend webhook request body (spec §3.8). Best-effort, never affects the send response.</summary>
public sealed record PostSendWebhookRequest(Guid RoomId, ulong Seq, string SenderId, string Content, DateTimeOffset Time, string Source);

/// <summary>SSE `message`-event payload (spec §3.9). `Replayed` is omitted (false) for live delivery.</summary>
public sealed record SseMessagePayload(Guid RoomId, ulong Seq, string SenderId, DateTimeOffset Time, string Content, bool Replayed);

/// <summary>SSE `revoked`-event payload (spec §3.9).</summary>
public sealed record SseRevokedPayload(Guid RoomId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(SendMessageResponse))]
[JsonSerializable(typeof(RoomMessage))]
[JsonSerializable(typeof(RoomLiveMessage))]
[JsonSerializable(typeof(RoomMessageDelivery))]
[JsonSerializable(typeof(TypingNotification))]
[JsonSerializable(typeof(PresenceDelta))]
[JsonSerializable(typeof(HistoryMessage))]
[JsonSerializable(typeof(HistoryMessage[]))]
[JsonSerializable(typeof(RoomHistoryResponse))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SetUserRoleRequest))]
[JsonSerializable(typeof(UserRoleResponse))]
[JsonSerializable(typeof(AddMembersRequest))]
[JsonSerializable(typeof(RoomMembersResponse))]
[JsonSerializable(typeof(UserRoomsResponse))]
[JsonSerializable(typeof(MessageRetracted))]
[JsonSerializable(typeof(MembershipRevoked))]
[JsonSerializable(typeof(InjectMessageRequest))]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(RedactMessageResponse))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(AuditEntry[]))]
[JsonSerializable(typeof(AuditPageResponse))]
[JsonSerializable(typeof(AuditVerificationResponse))]
[JsonSerializable(typeof(AuditCheckpointEntry))]
[JsonSerializable(typeof(AuditCheckpointEntry[]))]
[JsonSerializable(typeof(AuditCheckpointsResponse))]
[JsonSerializable(typeof(PresendWebhookRequest))]
[JsonSerializable(typeof(PresendWebhookResponse))]
[JsonSerializable(typeof(PostSendWebhookRequest))]
[JsonSerializable(typeof(SseMessagePayload))]
[JsonSerializable(typeof(SseRevokedPayload))]
[JsonSerializable(typeof(RetentionResponse))]
[JsonSerializable(typeof(SetRetentionRequest))]
public partial class DotwireJsonContext : JsonSerializerContext;

public static class Messages
{
    public static void MapMessageEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/rooms/{roomId}/messages")
            .RequireAuthorization()
            .AddEndpointFilter<RoleCrossCheckFilter>()
            .AddEndpointFilter<RoomMembershipFilter>();

        group.MapPost("/", async (
            string roomId,
            SendMessageRequest request,
            HttpContext http,
            MessageCipher cipher,
            INatsJSContext js,
            INatsConnection nats,
            IOptions<DotwireOptions> dotwireOptions,
            IOptions<NatsOptions> natsOptions,
            Dotwire.Nats.PresendWebhookClient presendClient,
            Dotwire.Nats.PostSendWebhookService postSendService,
            ILoggerFactory loggerFactory) =>
        {
            var roomGuid = Guid.Parse(roomId);
            var logger = loggerFactory.CreateLogger("Dotwire.Api.Messages");
            var maxBytes = dotwireOptions.Value.MaxContentBytes;

            // 1. Validate content and sender id (spec §3.2 step 1, §3.14).
            if (string.IsNullOrEmpty(request.Content))
                return ApiResults.InvalidContent();
            if (Encoding.UTF8.GetByteCount(request.Content) > maxBytes)
                return ApiResults.InvalidContent();

            var senderId = http.User.FindFirstValue("sub")!;
            if (!IdValidation.IsValid(senderId))
                return ApiResults.InvalidUserId();

            var content = request.Content;
            var time = DateTimeOffset.UtcNow;

            // 2. Presend webhook (spec §3.8) - runs whenever Dotwire:Webhooks:Presend:Url is set.
            var verdict = await presendClient.EvaluateAsync(roomGuid, senderId, content, time, "member", http.RequestAborted);
            switch (verdict.Outcome)
            {
                case Dotwire.Nats.PresendOutcome.Rejected:
                    return ApiResults.PresendRejected(verdict.Reason);
                case Dotwire.Nats.PresendOutcome.Unavailable:
                    return ApiResults.PresendUnavailable();
                case Dotwire.Nats.PresendOutcome.AllowedWithRewrite:
                    if (Encoding.UTF8.GetByteCount(verdict.Content!) > maxBytes)
                        return ApiResults.InvalidContent();
                    content = verdict.Content!;
                    break;
            }

            var plaintext = Encoding.UTF8.GetBytes(content);

            MessagePublisher.PublishResult result;
            try
            {
                // 3-4. Encrypt, publish RoomMessage to JetStream, await the ack, then
                //      best-effort republish a seq-tagged live message on NATS core.
                result = await MessagePublisher.PublishAsync(
                    roomGuid, senderId, plaintext, cipher, js, nats, natsOptions.Value, logger, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("JetStream publish failed for room {RoomId}: {Error}", roomGuid, ex.Message);
                return ApiResults.StreamUnavailable();
            }

            // 5. Postsend webhook (spec §3.8) - enqueued, best-effort, never blocks the response.
            postSendService.Enqueue(new PostSendWebhookRequest(roomGuid, result.Seq, senderId, content, result.Time, "member"));

            // 6. Return the ack.
            return Results.Accepted(value: new SendMessageResponse(result.Seq, result.Time));
        }).RequireRateLimiting("send");

        group.MapGet("/", async (
            string roomId,
            ulong? afterSeq,
            ulong? beforeSeq,
            int? limit,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            MessageCipher cipher,
            HttpContext http,
            ILoggerFactory loggerFactory) =>
        {
            var roomGuid = Guid.Parse(roomId);
            var pageSize = Math.Clamp(limit ?? 50, 1, 100);
            var ct = http.RequestAborted;
            var isAscending = afterSeq.HasValue;

            var (rawRows, hasMore) = await QueryHistoryPageAsync(readDataSource, roomGuid, afterSeq, beforeSeq, pageSize, ct);

            if (!isAscending)
            {
                rawRows.Reverse();
            }

            var logger = loggerFactory.CreateLogger("Dotwire.Api.Messages");
            var messages = new List<HistoryMessage>(rawRows.Count);
            foreach (var row in rawRows)
            {
                try
                {
                    var decrypted = cipher.Decrypt(row.KeyId, row.Content);
                    var text = Encoding.UTF8.GetString(decrypted);
                    messages.Add(new HistoryMessage(row.Seq, row.Time, row.SenderId, text));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Failed to decrypt message {Seq} in room {RoomId}: {Error}", row.Seq, roomGuid, ex.Message);
                }
            }

            return Results.Ok(new RoomHistoryResponse(roomGuid, messages.ToArray(), hasMore));
        }).RequireRateLimiting("read");
    }

    /// <summary>Shared history-page query, reused by the REST history endpoint and SSE replay (spec §3.9).</summary>
    internal static async Task<(List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)> Rows, bool HasMore)> QueryHistoryPageAsync(
        NpgsqlDataSource readDataSource, Guid roomGuid, ulong? afterSeq, ulong? beforeSeq, int pageSize, CancellationToken ct)
    {
        string sql;
        if (afterSeq.HasValue)
        {
            sql = "SELECT seq, time, sender_id, key_id, content FROM messages WHERE room_id = $1 AND seq > $2 ORDER BY seq ASC LIMIT $3";
        }
        else if (beforeSeq.HasValue)
        {
            sql = "SELECT seq, time, sender_id, key_id, content FROM messages WHERE room_id = $1 AND seq < $2 ORDER BY seq DESC LIMIT $3";
        }
        else
        {
            sql = "SELECT seq, time, sender_id, key_id, content FROM messages WHERE room_id = $1 ORDER BY seq DESC LIMIT $2";
        }

        var rawRows = new List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)>();

        await using (var cmd = readDataSource.CreateCommand(sql))
        {
            cmd.Parameters.AddWithValue(roomGuid);
            if (afterSeq.HasValue)
            {
                cmd.Parameters.AddWithValue((long)afterSeq.Value);
                cmd.Parameters.AddWithValue(pageSize + 1);
            }
            else if (beforeSeq.HasValue)
            {
                cmd.Parameters.AddWithValue((long)beforeSeq.Value);
                cmd.Parameters.AddWithValue(pageSize + 1);
            }
            else
            {
                cmd.Parameters.AddWithValue(pageSize + 1);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var seq = (ulong)reader.GetInt64(0);
                var time = reader.GetFieldValue<DateTimeOffset>(1);
                var senderId = reader.GetString(2);
                var keyId = reader.GetString(3);
                var content = (byte[])reader.GetValue(4);
                rawRows.Add((seq, time, senderId, keyId, content));
            }
        }

        var hasMore = rawRows.Count > pageSize;
        if (hasMore)
        {
            rawRows.RemoveAt(rawRows.Count - 1);
        }

        return (rawRows, hasMore);
    }
}
