using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dotwire.Data;

public static class MessageCompression
{
    public static bool Supported(string? license, string? version)
    {
        if (license != "timescale" || string.IsNullOrEmpty(version)) return false;
        var core = version.Split('-')[0];
        return Version.TryParse(core, out var parsed) && parsed >= new Version(2, 11);
    }

    public static async Task ApplyAsync(NpgsqlConnection connection, int afterDays, ILogger logger, CancellationToken cancellationToken)
    {
        string? license, version;
        await using (var cmd = new NpgsqlCommand(
            "SELECT current_setting('timescaledb.license', true), (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb')",
            connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            license = reader.IsDBNull(0) ? null : reader.GetString(0);
            version = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (!Supported(license, version))
        {
            if (afterDays >= 1)
                logger.LogWarning(
                    "Dotwire:Compression:AfterDays is set but compression is unsupported (timescaledb.license={License}, version={Version}); ignoring",
                    license ?? "(none)", version ?? "(none)");
            return;
        }
        foreach (var (table, orderBy) in Tables)
            await ApplyTableAsync(connection, table, orderBy, afterDays, logger, cancellationToken);
    }

    private static readonly (string Table, string OrderBy)[] Tables =
    [
        ("messages", "seq, time"),
        ("access_log", "time DESC, stream_seq"),
    ];

    private static async Task ApplyTableAsync(
        NpgsqlConnection connection, string table, string orderBy, int afterDays, ILogger logger, CancellationToken cancellationToken)
    {
        if (afterDays == 0)
        {
            await ExecuteAsync(connection, $"SELECT remove_compression_policy('{table}', if_exists => true)", cancellationToken);
            return;
        }

        bool enabled;
        await using (var cmd = new NpgsqlCommand(
            "SELECT compression_enabled FROM timescaledb_information.hypertables WHERE hypertable_name = $1", connection))
        {
            cmd.Parameters.AddWithValue(table);
            enabled = await cmd.ExecuteScalarAsync(cancellationToken) is true;
        }
        if (!enabled)
            await ExecuteAsync(connection,
                $"ALTER TABLE {table} SET (timescaledb.compress, timescaledb.compress_segmentby = 'room_id', timescaledb.compress_orderby = '{orderBy}')",
                cancellationToken);

        await ExecuteAsync(connection, $"SELECT remove_compression_policy('{table}', if_exists => true)", cancellationToken);
        await using (var cmd = new NpgsqlCommand($"SELECT add_compression_policy('{table}', make_interval(days => $1))", connection))
        {
            cmd.Parameters.AddWithValue(afterDays);
            await cmd.ExecuteScalarAsync(cancellationToken);
        }
        logger.LogInformation("Compression policy for {Table} set to compress chunks older than {Days} days", table, afterDays);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
