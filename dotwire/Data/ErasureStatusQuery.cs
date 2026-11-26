using Dotwire.Api;
using Dotwire.Configuration;
using Npgsql;

namespace Dotwire.Data;

public static class ErasureStatusQuery
{
    public static async Task<long?> OpenErasureIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT e.id FROM erasures e WHERE e.user_id = $1 " +
            "AND NOT EXISTS (SELECT 1 FROM erasure_completions c WHERE c.erasure_id = e.id) ORDER BY e.id LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue(userId);
        return (long?)await cmd.ExecuteScalarAsync(ct);
    }

    public static async Task<ErasureStatus?> GetAsync(NpgsqlDataSource read, long id, ComplianceOptions options, CancellationToken ct)
    {
        await using var conn = await read.OpenConnectionAsync(ct);
        return await GetAsync(conn, id, options, ct);
    }

    public static async Task<ErasureStatus[]> ListForUserAsync(NpgsqlDataSource read, string userId, ComplianceOptions options, CancellationToken ct)
    {
        await using var conn = await read.OpenConnectionAsync(ct);
        var ids = new List<long>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM erasures WHERE user_id = $1 ORDER BY id", conn))
        {
            cmd.Parameters.AddWithValue(userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetInt64(0));
        }

        var result = new List<ErasureStatus>(ids.Count);
        foreach (var id in ids)
        {
            if (await GetAsync(conn, id, options, ct) is { } status)
                result.Add(status);
        }

        return result.ToArray();
    }

    private static async Task<ErasureStatus?> GetAsync(NpgsqlConnection conn, long id, ComplianceOptions options, CancellationToken ct)
    {
        string userId, mode;
        DateTimeOffset requestedAt;
        DateTimeOffset? completedAt;
        await using (var cmd = new NpgsqlCommand(
            "SELECT e.user_id, e.mode, e.requested_at, c.completed_at FROM erasures e " +
            "LEFT JOIN erasure_completions c ON c.erasure_id = e.id WHERE e.id = $1", conn))
        {
            cmd.Parameters.AddWithValue(id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            userId = reader.GetString(0);
            mode = reader.GetString(1);
            requestedAt = reader.GetFieldValue<DateTimeOffset>(2);
            completedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
        }

        if (completedAt is not null)
            return new ErasureStatus(id, userId, mode, requestedAt, "completed", 0, [], completedAt);

        long remaining, eligible;
        bool held, young;
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*), " +
            "count(*) FILTER (WHERE b = 0), " +
            "coalesce(bool_or(b = -1), false), " +
            "coalesce(bool_or($3 > 0 AND t > now() - make_interval(days => $3)), false) " +
            "FROM (SELECT time AS t, message_change_block(room_id, sender_id, time, $3, $2) AS b FROM messages WHERE sender_id = $1) m", conn))
        {
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(options.AllowChanges);
            cmd.Parameters.AddWithValue(options.MinimumKeepDays);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            remaining = reader.GetInt64(0);
            eligible = reader.GetInt64(1);
            held = reader.GetBoolean(2);
            young = reader.GetBoolean(3);
        }

        if (remaining == 0 || eligible > 0)
            return new ErasureStatus(id, userId, mode, requestedAt, "in_progress", remaining, [], null);

        var blockedBy = new List<string>();
        if (held)
            blockedBy.Add("legal_hold");
        if (!options.AllowChanges)
            blockedBy.Add("changes_not_allowed");
        if (young)
            blockedBy.Add("minimum_keep");
        return new ErasureStatus(id, userId, mode, requestedAt, "waiting", remaining, blockedBy.ToArray(), null);
    }
}
