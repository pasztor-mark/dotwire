using NpgsqlTypes;
using Npgsql;

namespace Dotwire.Data;

public interface ILsnSource
{
    Task<ulong> PrimaryLsnAsync(CancellationToken ct);

    Task<ulong?> ReplicaReplayLsnAsync(CancellationToken ct);
}

public sealed class PostgresLsnSource(
    [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
    [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource) : ILsnSource
{
    public async Task<ulong> PrimaryLsnAsync(CancellationToken ct)
    {
        await using var cmd = writeDataSource.CreateCommand("SELECT pg_current_wal_lsn()::text");
        return (ulong)NpgsqlLogSequenceNumber.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
    }

    public async Task<ulong?> ReplicaReplayLsnAsync(CancellationToken ct)
    {
        await using var cmd = readDataSource.CreateCommand(
            "SELECT CASE WHEN pg_is_in_recovery() THEN coalesce(pg_last_wal_replay_lsn(), '0/0'::pg_lsn)::text END");
        return await cmd.ExecuteScalarAsync(ct) is string lsn ? (ulong)NpgsqlLogSequenceNumber.Parse(lsn) : null;
    }
}

public static class ReplicaCatchUp
{
    public static async Task WaitForReplicaAsync(ILsnSource source, ulong target, TimeSpan poll, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await source.ReplicaReplayLsnAsync(ct) is not { } replayed || replayed >= target)
                return;

            await Task.Delay(poll, ct);
        }
    }
}
