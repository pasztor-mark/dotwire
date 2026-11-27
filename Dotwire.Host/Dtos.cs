using System.Text.Json.Serialization;

namespace Dotwire.Host;

// ---- Messages -------------------------------------------------------------

/// <summary>The send ack. Seq is the JetStream ordering token; it is what a later redaction targets.</summary>
public sealed record SendMessageResult(ulong Seq, DateTimeOffset Time);

public sealed record HistoryMessage(ulong Seq, DateTimeOffset Time, string SenderId, string Content);

public sealed record RoomHistory(Guid RoomId, IReadOnlyList<HistoryMessage> Messages, bool HasMore);

public sealed record RedactMessageResult(Guid RoomId, ulong Seq, bool RemovedFromHistory, bool RemovedFromStream);

public sealed record HistoryQuery(ulong? AfterSeq = null, ulong? BeforeSeq = null, int? Limit = null);

// ---- Roles / membership -----------------------------------------------------

public sealed record UserRole(string UserId, string Role);

public sealed record RoomMembers(Guid RoomId, IReadOnlyList<string> Members);

public sealed record UserRooms(string UserId, IReadOnlyList<Guid> RoomIds);

// ---- DSAR export -----------------------------------------------------------

public sealed record UserExportMessage(
    Guid RoomId,
    ulong Seq,
    DateTimeOffset Time,
    string? Content,
    bool Undecryptable = false);

public sealed record UserExportAuditEvent(
    long Id,
    string EventType,
    Guid? RoomId,
    ulong? MessageSeq,
    string? ActorId,
    string? SubjectId,
    long? Value,
    DateTimeOffset Time);

public sealed record UserExport(
    string UserId,
    DateTimeOffset ExportedAt,
    string? Role,
    IReadOnlyList<Guid> RoomIds,
    IReadOnlyList<UserExportMessage> Messages,
    IReadOnlyList<UserExportAuditEvent> AuditEvents,
    IReadOnlyList<UserExportAccessEvent> AccessLog);

public sealed record UserExportAccessEvent(
    DateTimeOffset Time,
    string Event,
    Guid RoomId,
    string UserId,
    string? ActorId,
    string Role,
    string Via,
    Guid? WindowId,
    DateTimeOffset? ExpiresAt,
    string? Reason,
    ulong? FromSeq,
    ulong? ToSeq,
    int? Count);

public sealed record AccessLogQuery(
    DateTimeOffset From,
    DateTimeOffset? To = null,
    Guid? RoomId = null,
    string? UserId = null,
    int? Limit = null,
    string? Cursor = null);

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

public sealed record AccessLogPage(IReadOnlyList<AccessLogEntry> Entries, string? Next);

internal sealed record AccessLogPageDto(AccessLogEntry[] Entries, string? Next);

// ---- Audit -------------------------------------------------------------------

/// <summary>A durable, hash-chained audit_log row as returned by `/audit*`. Hash is lowercase hex.</summary>
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

public sealed record AuditPage(IReadOnlyList<AuditEntry> Events, bool HasMore);

public sealed record AuditVerification(
    bool Ok,
    long FromId,
    long ToId,
    long Checked,
    long? FirstInvalidId,
    DateOnly? Anchor);

public sealed record AuditCheckpoint(DateOnly Day, long LastId, string Hash, DateTimeOffset CreatedAt);

public sealed record AuditArchiveResult(long Count, long LastId, string? LastHash);

// ---- Retention -----------------------------------------------------------------

public sealed record RetentionDto(int MessagesDays);

public sealed record ComplianceSettings(bool AllowChanges, int MinimumKeepDays, string ErasureMode, int ActiveHolds, bool RetentionPaused, bool AccessLogEnabled, int AccessLogRetentionDays);

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

public enum EmergencyDeletionReason
{
    IllegalContent,
    CourtOrder,
}

public enum ErasureMode
{
    Anonymize,
    Delete,
}

public sealed record ErasureStatus(
    long Id,
    string UserId,
    string Mode,
    DateTimeOffset RequestedAt,
    string Status,
    long Remaining,
    IReadOnlyList<string> BlockedBy,
    DateTimeOffset? CompletedAt);

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
    IReadOnlyList<ErasedMessageRef>? Messages = null);

// ---- Hooks -----------------------------------------------------------------

public sealed record PresendContext(Guid RoomId, string Content, string SenderUserId);

public sealed record PresendResult(bool Allow, string? Content = null, string? RejectionReason = null)
{
    public static PresendResult Accept(string? rewrittenContent = null) => new(true, rewrittenContent);
    public static PresendResult Reject(string reason) => new(false, RejectionReason: reason);
}

/// <summary>What a PostSend hook sees: the accepted message plus its ack, so it can be found again by (RoomId, Seq).</summary>
public sealed record PostSendContext(Guid RoomId, ulong Seq, DateTimeOffset Time, string Content, string SenderUserId);

// ---- Client-send / webhook handler results -----------------------------------

/// <summary>
/// The exact HTTP response for a client-initiated send routed through <see cref="DotwireHostClient.HandleClientSendAsync"/>.
/// <see cref="Json"/> is the exact body to write back: the ack on success, `{error,reason}` on failure.
/// </summary>
public sealed record ClientSendResult(int StatusCode, string Json, SendMessageResult? Ack, string? ErrorCode, string? Reason);

/// <summary>The exact HTTP response for a presend/postsend webhook call routed through the host.</summary>
public sealed record WebhookResult(int StatusCode, string Json);

public sealed record WebhookHeaders(string? Signature, string? Timestamp = null, string? SignatureV2 = null);

// ---- Wire DTOs (server shapes; internal, not part of the public surface) ----

internal sealed record ClientSendRequest(Guid RoomId, string Content, string? IdempotencyKey = null);

internal sealed record ApiErrorBody(string Error, string? Reason = null);

internal sealed record SetUserRoleRequestDto(string Role);

internal sealed record AddMembersRequestDto(string[] UserIds, bool? EnsureMemberRole = null);

internal sealed record SendMessageRequestDto(string Content);

internal sealed record InjectMessageRequestDto(string? Content, string? SenderId);

internal sealed record SetRetentionRequestDto(int MessagesDays);

/// <summary>Presend webhook request body sent by the server (spec §3.8).</summary>
internal sealed record PresendWebhookRequestDto(Guid RoomId, string SenderId, string Content, DateTimeOffset Time, string Source);

/// <summary>Presend webhook response body: `{allow:true,content?}` or `{allow:false,reason?}`.</summary>
internal sealed record PresendWebhookResponseDto(bool Allow, string? Content = null, string? Reason = null);

/// <summary>Postsend webhook request body sent by the server (spec §3.8).</summary>
internal sealed record PostSendWebhookRequestDto(Guid RoomId, ulong Seq, string SenderId, string Content, DateTimeOffset Time, string Source);

internal sealed record EmptyResponseDto;

internal sealed record PlaceLegalHoldRequestDto(Guid? RoomId, string? UserId, string MatterRef);

internal sealed record EmergencyDeleteRequestDto(string Reason);

internal sealed record EraseUserRequestDto(string? Mode);

/// <summary>Wire shape of `GET /audit/checkpoints`: `{checkpoints:[...]}`.</summary>
internal sealed record AuditCheckpointsResponseDto(AuditCheckpoint[] Checkpoints);

internal sealed record AuditCheckpointLineDto(AuditCheckpoint Checkpoint);

/// <summary>SSE `message`-event payload (mirrors the server's SseMessagePayload).</summary>
internal sealed record SseMessagePayloadDto(Guid RoomId, ulong Seq, string SenderId, DateTimeOffset Time, string Content, bool Replayed);

/// <summary>SSE `revoked`-event payload.</summary>
internal sealed record SseRevokedPayloadDto(Guid RoomId);

/// <summary>SSE `typing`-event payload (ephemeral TypingNotification).</summary>
internal sealed record TypingNotificationDto(Guid RoomId, string UserId, bool IsTyping);

/// <summary>SSE `presence`-event payload (ephemeral PresenceDelta).</summary>
internal sealed record PresenceDeltaDto(Guid RoomId, string[] Joined, string[] Left);

/// <summary>SSE `retracted`-event payload (ephemeral MessageRetracted).</summary>
internal sealed record MessageRetractedDto(Guid RoomId, ulong Seq);

internal sealed record MessagesErasedDto(Guid RoomId, string Mode, ulong[] Seqs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SendMessageResult))]
[JsonSerializable(typeof(HistoryMessage))]
[JsonSerializable(typeof(HistoryMessage[]))]
[JsonSerializable(typeof(RoomHistory))]
[JsonSerializable(typeof(RedactMessageResult))]
[JsonSerializable(typeof(UserRole))]
[JsonSerializable(typeof(RoomMembers))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(UserRooms))]
[JsonSerializable(typeof(UserExportMessage))]
[JsonSerializable(typeof(UserExportMessage[]))]
[JsonSerializable(typeof(UserExportAuditEvent))]
[JsonSerializable(typeof(UserExportAuditEvent[]))]
[JsonSerializable(typeof(UserExport))]
[JsonSerializable(typeof(UserExportAccessEvent))]
[JsonSerializable(typeof(UserExportAccessEvent[]))]
[JsonSerializable(typeof(AccessLogEntry))]
[JsonSerializable(typeof(AccessLogEntry[]))]
[JsonSerializable(typeof(AccessLogPageDto))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(AuditEntry[]))]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(AuditVerification))]
[JsonSerializable(typeof(AuditCheckpoint))]
[JsonSerializable(typeof(AuditCheckpoint[]))]
[JsonSerializable(typeof(AuditCheckpointsResponseDto))]
[JsonSerializable(typeof(AuditCheckpointLineDto))]
[JsonSerializable(typeof(RetentionDto))]
[JsonSerializable(typeof(ClientSendRequest))]
[JsonSerializable(typeof(ApiErrorBody))]
[JsonSerializable(typeof(SetUserRoleRequestDto))]
[JsonSerializable(typeof(AddMembersRequestDto))]
[JsonSerializable(typeof(SendMessageRequestDto))]
[JsonSerializable(typeof(InjectMessageRequestDto))]
[JsonSerializable(typeof(SetRetentionRequestDto))]
[JsonSerializable(typeof(PresendWebhookRequestDto))]
[JsonSerializable(typeof(PresendWebhookResponseDto))]
[JsonSerializable(typeof(PostSendWebhookRequestDto))]
[JsonSerializable(typeof(EmptyResponseDto))]
[JsonSerializable(typeof(SseMessagePayloadDto))]
[JsonSerializable(typeof(SseRevokedPayloadDto))]
[JsonSerializable(typeof(TypingNotificationDto))]
[JsonSerializable(typeof(PresenceDeltaDto))]
[JsonSerializable(typeof(MessageRetractedDto))]
[JsonSerializable(typeof(MessagesErasedDto))]
[JsonSerializable(typeof(ComplianceSettings))]
[JsonSerializable(typeof(LegalHold))]
[JsonSerializable(typeof(LegalHold[]))]
[JsonSerializable(typeof(PlaceLegalHoldRequestDto))]
[JsonSerializable(typeof(EmergencyDeleteRequestDto))]
[JsonSerializable(typeof(EraseUserRequestDto))]
[JsonSerializable(typeof(ErasureStatus))]
[JsonSerializable(typeof(ErasureStatus[]))]
[JsonSerializable(typeof(ErasedMessageRef))]
[JsonSerializable(typeof(ErasureNotice))]
internal partial class DotwireHostJsonContext : JsonSerializerContext;
