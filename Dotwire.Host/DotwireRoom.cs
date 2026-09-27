namespace Dotwire.Host;

/// <summary>
/// A room-scoped view of <see cref="IDotwireHostClient"/>'s room methods, so callers working
/// against one room don't repeat its id on every call. Get one via <see cref="IDotwireHostClient.Room"/>.
/// </summary>
public sealed class DotwireRoom(IDotwireHostClient client, Guid roomId)
{
    public Guid RoomId { get; } = roomId;

    public Task<IReadOnlyList<string>> AddMembersAsync(IEnumerable<string> userIds, bool ensureMemberRole = true, CancellationToken ct = default) =>
        client.AddRoomMembersAsync(RoomId, userIds, ensureMemberRole, ct);

    public Task RemoveMemberAsync(string userId, CancellationToken ct = default) =>
        client.RemoveRoomMemberAsync(RoomId, userId, ct);

    public Task<IReadOnlyList<string>> GetMembersAsync(CancellationToken ct = default) =>
        client.GetRoomMembersAsync(RoomId, ct);

    public Task<IReadOnlyList<string>> GetParticipantsAsync(string asUserId, CancellationToken ct = default) =>
        client.GetRoomParticipantsAsync(RoomId, asUserId, ct);

    public Task<SendMessageResult> SendAsUserAsync(string userId, string content, CancellationToken ct = default) =>
        client.SendAsUserAsync(RoomId, userId, content, ct);

    public Task<SendMessageResult> SendSystemMessageAsync(string content, string senderId = "system", CancellationToken ct = default) =>
        client.SendSystemMessageAsync(RoomId, content, senderId, ct);

    public Task<RoomHistory> GetMessagesAsync(string asUserId, HistoryQuery? query = null, CancellationToken ct = default) =>
        client.GetMessagesAsync(RoomId, asUserId, query, ct);

    public IAsyncEnumerable<RoomEvent> StreamEventsAsync(string asUserId, StreamOptions? options = null, CancellationToken ct = default) =>
        client.StreamEventsAsync(RoomId, asUserId, options, ct);

    public Task<RedactMessageResult> RedactAsync(ulong seq, CancellationToken ct = default) =>
        client.RedactMessageAsync(RoomId, seq, ct);
}
