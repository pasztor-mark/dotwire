using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Dotwire.Data;

/// <summary>
/// Applies embedded SQL migrations (<c>Migrations/NNNN_*.sql</c>) at startup, under a
/// Postgres advisory lock - node roles mean several processes can start at once and must
/// not race the schema (docs/pre-implementation.md §1.2).
/// Runs on the migrator connection (owner role); the app itself runs as
/// <c>dotwire_app</c>, whose revoked grants enforce the append-only audit table.
/// </summary>
public static class MigrationRunner
{
    // Arbitrary but stable app-wide key ("dotwire" as hex); session-scoped, so it also
    // releases if the process dies mid-run.
    private const long AdvisoryLockKey = 0x646F_7477_6972_65;

    /// <summary>First line of a script that must run outside a transaction (e.g. CREATE EXTENSION quirks).</summary>
    private const string NoTransactionMarker = "-- dotwire:no-transaction";

    private const string ResourcePrefix = "Dotwire.Migrations.";

    public static async Task RunAsync(
        string migratorConnectionString,
        string? appRolePassword,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(appRolePassword))
            throw new InvalidOperationException(
                "Postgres:AppRolePassword must be set (POSTGRES_APP_PASSWORD in .env) . " +
                "migrations maintain the dotwire_app login and the runtime pools connect as it.");

        await using var conn = await OpenWithRetryAsync(migratorConnectionString, logger, ct);

        await ExecAsync(conn, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await ExecAsync(conn,
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    filename   text        PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            await ExecAsync(conn, "ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum text", ct);

            var applied = new Dictionary<string, string?>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT filename, checksum FROM schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    applied[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var scripts = LoadScripts().ToList();
            var modified = ModifiedScripts(applied, scripts);
            if (modified.Count > 0)
                throw new InvalidOperationException(
                    $"Applied migrations were modified after being applied: {string.Join(", ", modified)}. Add a new migration instead.");

            foreach (var (name, sql) in scripts)
            {
                var checksum = Checksum(sql);
                if (applied.TryGetValue(name, out var stored))
                {
                    if (stored is null)
                        await BackfillChecksumAsync(conn, name, checksum, ct);
                    continue;
                }

                if (sql.StartsWith(NoTransactionMarker, StringComparison.Ordinal))
                {
                    await ExecAsync(conn, sql, ct);
                    await RecordAsync(conn, name, checksum, transaction: null, ct);
                }
                else
                {
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await ExecAsync(conn, sql, ct);
                    await RecordAsync(conn, name, checksum, tx, ct);
                    await tx.CommitAsync(ct);
                }

                logger.LogInformation("Applied migration {Migration}", name);
            }

            // Refreshed every startup so a rotated POSTGRES_APP_PASSWORD takes effect.
            // ALTER ROLE can't take a bind parameter for the password, hence the quoting.
            var escaped = appRolePassword.Replace("'", "''");
            await ExecAsync(conn, $"ALTER ROLE dotwire_app PASSWORD '{escaped}'", ct);
        }
        finally
        {
            await ExecAsync(conn, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    /// <summary>Embedded scripts, ordinal-sorted so the NNNN_ prefix dictates order.</summary>
    public static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resourceName in names)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var readerStream = new StreamReader(stream);
            yield return (resourceName[ResourcePrefix.Length..], readerStream.ReadToEnd());
        }
    }

    private static async Task<NpgsqlConnection> OpenWithRetryAsync(
        string connectionString, ILogger logger, CancellationToken ct)
    {
        const int maxAttempts = 15;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                return conn;
            }
            catch (NpgsqlException ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("Postgres not ready (attempt {Attempt}/{Max}): {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static string Checksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

    public static IReadOnlyList<string> ModifiedScripts(
        IReadOnlyDictionary<string, string?> applied, IEnumerable<(string Name, string Sql)> scripts) =>
        scripts
            .Where(s => applied.TryGetValue(s.Name, out var stored) && stored is not null && stored != Checksum(s.Sql))
            .Select(s => s.Name)
            .ToList();

    private static async Task BackfillChecksumAsync(NpgsqlConnection conn, string name, string checksum, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE schema_migrations SET checksum = $2 WHERE filename = $1 AND checksum IS NULL", conn);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(checksum);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordAsync(
        NpgsqlConnection conn, string name, string checksum, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO schema_migrations (filename, checksum) VALUES ($1, $2)", conn, transaction);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(checksum);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
