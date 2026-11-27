using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
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

public sealed record AccessEvent(
    string Event,
    DateTimeOffset Time,
    Guid RoomId,
    string UserId,
    string? ActorId,
    string Role,
    string Via,
    Guid? WindowId = null,
    DateTimeOffset? ExpiresAt = null,
    string? Reason = null,
    ulong? FromSeq = null,
    ulong? ToSeq = null,
    int? Count = null);

public sealed record EmergencyDeleteRequest(string? Reason);

public sealed record RedactMessageResponse(Guid RoomId, ulong Seq, bool RemovedFromHistory, bool RemovedFromStream);

/// <summary>Retention get/set response (spec §3.11). 0 = indefinite.</summary>
public sealed record RetentionResponse(int MessagesDays, bool Paused = false);

/// <summary>Retention set request (spec §3.11). 0 = indefinite, max 36500.</summary>
public sealed record SetRetentionRequest(int MessagesDays);

public sealed record PlaceHoldRequest(Guid? RoomId, string? UserId, string? MatterRef);

public sealed record LegalHold(
    long Id,
    string Scope,
    Guid? RoomId,
    string? UserId,
    string MatterRef,
    string PlacedBy,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy);

public sealed record EraseUserRequest(string? Mode);

public sealed record ErasureStatus(
    long Id,
    string UserId,
    string Mode,
    DateTimeOffset RequestedAt,
    string Status,
    long Remaining,
    string[] BlockedBy,
    DateTimeOffset? CompletedAt);

public sealed record ComplianceResponse(bool AllowChanges, int MinimumKeepDays, string ErasureMode, int ActiveHolds, bool RetentionPaused, bool AccessLogEnabled, int AccessLogRetentionDays);

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

public sealed record AccessLogEntry(
    string Kind,
    Guid RoomId,
    string UserId,
    string? ActorId,
    string Role,
    string Via,
    DateTimeOffset Start,
    Guid? WindowId,
    DateTimeOffset? End,
    string? EndReason,
    DateTimeOffset? ExpiresAt,
    ulong? FromSeq,
    ulong? ToSeq,
    int? Count);

public sealed record AccessLogPageResponse(AccessLogEntry[] Entries, string? Next);

/// <summary>New 4xx/5xx failure body shape (spec §3.14): `{ "error": "&lt;code&gt;", "reason"?: "&lt;text&gt;" }`.</summary>
public sealed record ApiErrorResponse(string Error, string? Reason = null);

/// <summary>Presend webhook request body (spec §3.8).</summary>
public sealed record PresendWebhookRequest(Guid RoomId, string SenderId, string Content, DateTimeOffset Time, string Source);

/// <summary>Presend webhook response body: `{allow:true, content?}` or `{allow:false, reason?}` (spec §3.8).</summary>
public sealed record PresendWebhookResponse(bool Allow, string? Content, string? Reason);

/// <summary>Postsend webhook request body (spec §3.8). Best-effort, never affects the send response.</summary>
public sealed record PostSendWebhookRequest(Guid RoomId, ulong Seq, string SenderId, string Content, DateTimeOffset Time, string Source);

public sealed record MessagesErased(Guid RoomId, string Mode, ulong[] Seqs);

public sealed record ErasedMessageRef(Guid RoomId, ulong Seq);

public sealed record ErasureNotice(
    string Event,
    DateTimeOffset Time,
    Guid? RoomId = null,
    ulong? Seq = null,
    string? Reason = null,
    string? UserId = null,
    string? Mode = null,
    long? ErasureId = null,
    string? Status = null,
    ErasedMessageRef[]? Messages = null);

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
[JsonSerializable(typeof(EmergencyDeleteRequest))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(AuditEntry[]))]
[JsonSerializable(typeof(AuditPageResponse))]
[JsonSerializable(typeof(AuditVerificationResponse))]
[JsonSerializable(typeof(AuditCheckpointEntry))]
[JsonSerializable(typeof(AuditCheckpointEntry[]))]
[JsonSerializable(typeof(AuditCheckpointsResponse))]
[JsonSerializable(typeof(AccessLogEntry))]
[JsonSerializable(typeof(AccessLogEntry[]))]
[JsonSerializable(typeof(AccessLogPageResponse))]
[JsonSerializable(typeof(PresendWebhookRequest))]
[JsonSerializable(typeof(PresendWebhookResponse))]
[JsonSerializable(typeof(PostSendWebhookRequest))]
[JsonSerializable(typeof(MessagesErased))]
[JsonSerializable(typeof(ErasedMessageRef))]
[JsonSerializable(typeof(ErasedMessageRef[]))]
[JsonSerializable(typeof(ErasureNotice))]
[JsonSerializable(typeof(SseMessagePayload))]
[JsonSerializable(typeof(SseRevokedPayload))]
[JsonSerializable(typeof(RetentionResponse))]
[JsonSerializable(typeof(SetRetentionRequest))]
[JsonSerializable(typeof(PlaceHoldRequest))]
[JsonSerializable(typeof(LegalHold))]
[JsonSerializable(typeof(LegalHold[]))]
[JsonSerializable(typeof(ComplianceResponse))]
[JsonSerializable(typeof(EraseUserRequest))]
[JsonSerializable(typeof(ErasureStatus))]
[JsonSerializable(typeof(ErasureStatus[]))]
[JsonSerializable(typeof(AccessEvent))]
public partial class DotwireJsonContext : JsonSerializerContext;

public static class Messages
{
    private static readonly TimeSpan TailReadTimeout = TimeSpan.FromSeconds(3);

    public static void MapMessageEndpoints(WebApplication app, bool hubSend)
    {
        var group = app.MapGroup("/rooms/{roomId}/messages")
            .RequireAuthorization();
        if (hubSend)
            group.AddEndpointFilter<SendRateLimitFilter>();
        group.AddEndpointFilter<RoomAccessFilter>();

        var post = group.MapPost("/", async (
            string roomId,
            SendMessageRequest request,
            HttpContext http,
            MessageSend sender) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid))
                return Results.NotFound();

            var senderId = http.User.FindFirstValue("sub")!;
            var (response, rejection) = await sender.SendAsync(
                roomGuid, senderId, request.Content, http.Request.Headers["Idempotency-Key"].ToString(), "rest", http.RequestAborted);

            return rejection is not null ? ToResult(rejection) : Results.Accepted(value: response);
        });
        if (!hubSend)
            post.RequireRateLimiting("send");

        group.MapGet("/", async (
            string roomId,
            ulong? afterSeq,
            ulong? beforeSeq,
            int? limit,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            MessageCipher cipher,
            RoomsStreamCache streams,
            IOptions<NatsOptions> natsOptions,
            HttpContext http,
            AccessLogPublisher accessLog,
            ILoggerFactory loggerFactory) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid))
                return Results.NotFound();
            var pageSize = Math.Clamp(limit ?? 50, 1, 100);
            var ct = http.RequestAborted;
            var isAscending = afterSeq.HasValue;

            var logger = loggerFactory.CreateLogger("Dotwire.Api.Messages");
            var (rawRows, hasMore) = await QueryHistoryPageAsync(
                readDataSource, streams, natsOptions.Value, logger, roomGuid, afterSeq, beforeSeq, pageSize, ct);

            if (!isAscending)
            {
                rawRows.Reverse();
            }

            var messages = new List<HistoryMessage>(rawRows.Count);
            foreach (var row in rawRows)
            {
                try
                {
                    var decrypted = cipher.Decrypt(row.KeyId, row.Content, roomGuid, row.SenderId);
                    var text = Encoding.UTF8.GetString(decrypted);
                    messages.Add(new HistoryMessage(row.Seq, row.Time, row.SenderId, text));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Failed to decrypt message {Seq} in room {RoomId}: {Error}", row.Seq, roomGuid, ex.Message);
                }
            }

            if (accessLog.IsEnabled)
            {
                try
                {
                    await accessLog.RecordAsync(HistoryReadEvent(http.User, roomGuid, AccessEvents.Rest, messages.Count,
                        messages.Count == 0 ? null : messages.Min(m => m.Seq),
                        messages.Count == 0 ? null : messages.Max(m => m.Seq)), ct);
                }
                catch (AccessLogUnavailableException)
                {
                    return ApiResults.AccessLogUnavailable();
                }
            }

            return Results.Ok(new RoomHistoryResponse(roomGuid, messages.ToArray(), hasMore));
        }).RequireRateLimiting("read");
    }

    public const int MaxIdempotencyKeyLength = 128;

    internal static AccessEvent HistoryReadEvent(ClaimsPrincipal user, Guid roomId, string via, int count, ulong? fromSeq, ulong? toSeq) =>
        new(AccessEvents.HistoryRead, DateTimeOffset.UtcNow, roomId,
            user.FindFirstValue("sub") ?? string.Empty,
            user.FindFirstValue("dw:actor"),
            user.FindFirstValue("dw:role") ?? string.Empty,
            via, FromSeq: fromSeq, ToSeq: toSeq, Count: count);

    internal static SendRejection? ValidateContent(string? content, int maxBytes) =>
        string.IsNullOrEmpty(content) || Encoding.UTF8.GetByteCount(content) > maxBytes ? new SendRejection("invalid_content") : null;

    internal static (SendRejection? Error, string Content) ApplyPresend(Dotwire.Nats.PresendVerdict verdict, string content, int maxBytes) =>
        verdict.Outcome switch
        {
            Dotwire.Nats.PresendOutcome.Rejected => (new SendRejection("presend_rejected", verdict.Reason), content),
            Dotwire.Nats.PresendOutcome.Unavailable => (new SendRejection("presend_unavailable"), content),
            Dotwire.Nats.PresendOutcome.AllowedWithRewrite when ValidateContent(verdict.Content, maxBytes) is { } invalid => (invalid, content),
            Dotwire.Nats.PresendOutcome.AllowedWithRewrite => (null, verdict.Content!),
            _ => (null, content),
        };

    internal static IResult ToResult(SendRejection rejection) =>
        rejection.Code switch
        {
            "invalid_content" => ApiResults.InvalidContent(),
            "invalid_user_id" => ApiResults.InvalidUserId(),
            "invalid_request" => ApiResults.InvalidRequest(rejection.Reason),
            "presend_rejected" => ApiResults.PresendRejected(rejection.Reason),
            "presend_unavailable" => ApiResults.PresendUnavailable(),
            _ => ApiResults.StreamUnavailable(),
        };

    internal static int TailReadBudget(int limit, bool keepNewest) => keepNewest ? limit * 2 : limit;

    /// <summary>Shared history-page query, reused by the REST history endpoint and SSE replay (spec §3.9).</summary>
    internal static async Task<(List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)> Rows, bool HasMore)> QueryHistoryPageAsync(
        NpgsqlDataSource readDataSource, RoomsStreamCache streams, NatsOptions natsOptions, ILogger logger,
        Guid roomGuid, ulong? afterSeq, ulong? beforeSeq, int pageSize, CancellationToken ct)
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

        if (!natsOptions.Enabled || beforeSeq.HasValue || (afterSeq.HasValue && hasMore))
            return (rawRows, hasMore);

        var newest = !afterSeq.HasValue;
        var persistedTail = newest
            ? rawRows.Count > 0 ? rawRows[0].Seq : 0
            : rawRows.Count > 0 ? rawRows[^1].Seq : afterSeq!.Value;

        List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)> tail;
        using var tailDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tailDeadline.CancelAfter(TailReadTimeout);
        try
        {
            tail = await ReadUnpersistedTailAsync(
                readDataSource, streams, natsOptions, roomGuid, persistedTail, newest ? pageSize + 1 : pageSize - rawRows.Count + 1, newest, tailDeadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("JetStream tail read failed for room {RoomId}, serving Postgres history only: {Error}", roomGuid, ex.Message);
            return (rawRows, hasMore);
        }

        if (tail.Count == 0)
            return (rawRows, hasMore);

        if (newest)
        {
            tail.Reverse();
            rawRows.InsertRange(0, tail);
        }
        else
        {
            rawRows.AddRange(tail);
        }

        if (rawRows.Count > pageSize)
        {
            rawRows.RemoveRange(pageSize, rawRows.Count - pageSize);
            hasMore = true;
        }

        return (rawRows, hasMore);
    }

    private static async Task<List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)>> ReadUnpersistedTailAsync(
        NpgsqlDataSource readDataSource, RoomsStreamCache streams, NatsOptions natsOptions,
        Guid roomGuid, ulong afterSeq, int limit, bool keepNewest, CancellationToken ct)
    {
        var rows = new List<(ulong Seq, DateTimeOffset Time, string SenderId, string KeyId, byte[] Content)>();
        var subject = $"{natsOptions.RoomSubjectPrefix}{roomGuid}";
        var stream = await streams.GetAsync(ct);
        var maxReads = TailReadBudget(limit, keepNewest);
        var next = afterSeq + 1;

        for (var reads = 0; reads < maxReads; reads++)
        {
            StreamMsgGetResponse response;
            try
            {
                response = await stream.GetAsync(new StreamMsgGetRequest { Seq = next, NextBySubj = subject }, ct);
            }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10037)
            {
                break;
            }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10059)
            {
                streams.Invalidate();
                throw;
            }

            var stored = response.Message;
            next = stored.Seq + 1;
            var message = JsonSerializer.Deserialize(stored.Data.Span, DotwireJsonContext.Default.RoomMessage);
            if (message is null || message.RoomId != roomGuid || string.IsNullOrEmpty(message.KeyId) || message.Content is not { Length: > 0 })
                continue;

            rows.Add((stored.Seq, message.Time, message.SenderId, message.KeyId, message.Content));
            if (keepNewest && rows.Count > limit)
                rows.RemoveAt(0);
        }

        if (rows.Count == 0)
            return rows;

        await using var cmd = readDataSource.CreateCommand(
            "SELECT seq FROM message_redactions WHERE room_id = $1 AND seq = ANY($2)");
        cmd.Parameters.AddWithValue(roomGuid);
        cmd.Parameters.AddWithValue(rows.Select(r => (long)r.Seq).ToArray());
        var redacted = new HashSet<ulong>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                redacted.Add((ulong)reader.GetInt64(0));
        }

        rows.RemoveAll(r => redacted.Contains(r.Seq));
        return rows;
    }
}
