using Dotwire.Data;
using Npgsql;

namespace Dotwire.Auth;

public interface IAccessStore
{
    Task<string?> GetRoleAsync(string userId, CancellationToken ct);

    Task<bool> IsMemberAsync(Guid roomId, string userId, CancellationToken ct);

    Task<IReadOnlyDictionary<string, string>> GetRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken ct);

    Task<IReadOnlySet<(Guid RoomId, string UserId)>> FilterMembersAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct);
}

public sealed class PostgresAccessStore(
    [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource) : IAccessStore
{
    public async Task<string?> GetRoleAsync(string userId, CancellationToken ct)
    {
        await using var cmd = readDataSource.CreateCommand("SELECT role FROM user_roles WHERE user_id = $1");
        cmd.Parameters.AddWithValue(userId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<bool> IsMemberAsync(Guid roomId, string userId, CancellationToken ct)
    {
        await using var cmd = readDataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM room_members WHERE room_id = $1 AND user_id = $2)");
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue(userId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userIds.Count == 0)
            return roles;

        await using var cmd = readDataSource.CreateCommand("SELECT user_id, role FROM user_roles WHERE user_id = ANY($1)");
        cmd.Parameters.AddWithValue(userIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            roles[reader.GetString(0)] = reader.GetString(1);
        return roles;
    }

    public async Task<IReadOnlySet<(Guid RoomId, string UserId)>> FilterMembersAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct)
    {
        var members = new HashSet<(Guid, string)>();
        if (pairs.Count == 0)
            return members;

        await using var cmd = readDataSource.CreateCommand(
            "SELECT m.room_id, m.user_id FROM room_members m " +
            "JOIN unnest($1::uuid[], $2::text[]) AS p(room_id, user_id) " +
            "ON m.room_id = p.room_id AND m.user_id = p.user_id");
        cmd.Parameters.AddWithValue(pairs.Select(p => p.RoomId).ToArray());
        cmd.Parameters.AddWithValue(pairs.Select(p => p.UserId).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            members.Add((reader.GetGuid(0), reader.GetString(1)));
        return members;
    }
}
