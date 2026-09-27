namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AddRoomMembersAsync(Guid roomId, IEnumerable<string> userIds, bool ensureMemberRole = true, CancellationToken ct = default)
    {
        var body = JsonBody(new AddMembersRequestDto(userIds.ToArray(), ensureMemberRole), DotwireHostJsonContext.Default.AddMembersRequestDto);
        var dto = await SendAsync(HttpMethod.Post, $"/admin/rooms/{roomId}/members", AdminToken(),
            DotwireHostJsonContext.Default.RoomMembers, ct, body);
        return dto.Members;
    }

    /// <inheritdoc />
    public Task RemoveRoomMemberAsync(Guid roomId, string userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/admin/rooms/{roomId}/members/{Uri.EscapeDataString(userId)}", AdminToken(), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetRoomMembersAsync(Guid roomId, CancellationToken ct = default)
    {
        var dto = await SendAsync(HttpMethod.Get, $"/admin/rooms/{roomId}/members", AdminToken(),
            DotwireHostJsonContext.Default.RoomMembers, ct);
        return dto.Members;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetRoomParticipantsAsync(Guid roomId, string asUserId, CancellationToken ct = default) =>
        WithResolvedRoleAsync(asUserId, async (role, innerCt) =>
        {
            var token = MintToken(asUserId, role, ttl: _options.DefaultTokenTtl);
            var userIds = await SendAsync(HttpMethod.Get, $"/rooms/{roomId}/participants", token,
                DotwireHostJsonContext.Default.StringArray, innerCt);
            return (IReadOnlyList<string>)userIds;
        }, ct);
}
