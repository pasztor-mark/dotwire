namespace Dotwire.Host;

/// <summary>
/// Host-side SDK surface: mints RS256 tokens with the host's private key and talks to dotwire
/// over HTTP. Every message reaches dotwire *through the host backend*, which is what makes
/// the moderation hooks (<see cref="Presend"/>/<see cref="PostSend"/>) both cheap and complete.
/// </summary>
public interface IDotwireHostClient
{
    /// <summary>
    /// Mints a token directly, bypassing role resolution entirely - the caller asserts the role.
    /// </summary>
    string MintToken(string userId, string? role = null, string? displayName = null, TimeSpan? ttl = null);

    Task<UserRole> SetUserRoleAsync(string userId, string role, CancellationToken ct = default);
    Task<UserRole?> GetUserRoleAsync(string userId, CancellationToken ct = default);
    Task DeleteUserRoleAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetUserRoomsAsync(string userId, CancellationToken ct = default);
    Task<UserExport> ExportUserAsync(string userId, CancellationToken ct = default);
    Task ExportUserAsync(string userId, Stream destination, CancellationToken ct = default);

    /// <summary>Room-scoped view of the members/send/history/stream/redact methods below, without repeating the room id.</summary>
    DotwireRoom Room(Guid roomId);

    Task<IReadOnlyList<string>> AddRoomMembersAsync(Guid roomId, IEnumerable<string> userIds, bool ensureMemberRole = true, CancellationToken ct = default);
    Task RemoveRoomMemberAsync(Guid roomId, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRoomMembersAsync(Guid roomId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRoomParticipantsAsync(Guid roomId, string asUserId, CancellationToken ct = default);

    Task<SendMessageResult> SendAsUserAsync(Guid roomId, string userId, string content, CancellationToken ct = default);
    Task<SendMessageResult> SendSystemMessageAsync(Guid roomId, string content, string senderId = "system", CancellationToken ct = default);
    Task<RoomHistory> GetMessagesAsync(Guid roomId, string asUserId, HistoryQuery? query = null, CancellationToken ct = default);
    IAsyncEnumerable<RoomEvent> StreamEventsAsync(Guid roomId, string asUserId, StreamOptions? options = null, CancellationToken ct = default);
    Task<RedactMessageResult> RedactMessageAsync(Guid roomId, ulong seq, CancellationToken ct = default);

    Task<AuditPage> ReadAuditLogAsync(long afterId = 0, int limit = 100, CancellationToken ct = default);
    Task<AuditVerification> VerifyAuditLogAsync(bool full = false, CancellationToken ct = default);
    Task<IReadOnlyList<AuditCheckpoint>> GetAuditCheckpointsAsync(int limit = 30, CancellationToken ct = default);

    Task<int> GetMessageRetentionDaysAsync(CancellationToken ct = default);
    Task<int> SetMessageRetentionDaysAsync(int days, CancellationToken ct = default);

    /// <summary>
    /// Parses `{roomId, content}` from a client-initiated send request, sends it as
    /// <paramref name="senderUserId"/> (running <see cref="Presend"/>/<see cref="PostSend"/>),
    /// and returns the exact HTTP response to write back.
    /// </summary>
    Task<ClientSendResult> HandleClientSendAsync(Stream body, string senderUserId, CancellationToken ct = default);

    /// <summary>
    /// Handles dotwire's presend webhook call-out: verifies the signature (if configured),
    /// runs <see cref="Presend"/> (default-allow if unset), and returns the exact HTTP
    /// response to write back.
    /// </summary>
    Task<WebhookResult> HandlePresendWebhookAsync(Stream body, string? signatureHeader, CancellationToken ct = default);

    /// <summary>
    /// Handles dotwire's postsend webhook call-out: verifies the signature (if configured),
    /// runs <see cref="PostSend"/>, and returns the exact HTTP response to write back.
    /// </summary>
    Task<WebhookResult> HandlePostSendWebhookAsync(Stream body, string? signatureHeader, CancellationToken ct = default);

    /// <summary>
    /// Runs in-process before a user-initiated send (<see cref="SendAsUserAsync"/>,
    /// <see cref="HandleClientSendAsync"/>) and inside <see cref="HandlePresendWebhookAsync"/> -
    /// one place covers direct SDK sends and the server's webhook call-outs. Does NOT run for
    /// <see cref="SendSystemMessageAsync"/> (host-authored injection).
    /// </summary>
    Func<PresendContext, CancellationToken, Task<PresendResult>>? Presend { get; set; }

    /// <summary>
    /// Runs after a user-initiated send is accepted (<see cref="SendAsUserAsync"/>,
    /// <see cref="HandleClientSendAsync"/>) and inside <see cref="HandlePostSendWebhookAsync"/>.
    /// Does NOT run for <see cref="SendSystemMessageAsync"/>.
    /// </summary>
    Func<PostSendContext, CancellationToken, Task>? PostSend { get; set; }
}
