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

            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT filename FROM schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    applied.Add(reader.GetString(0));
            }

            foreach (var (name, sql) in LoadScripts())
            {
                if (applied.Contains(name))
                    continue;

                if (sql.StartsWith(NoTransactionMarker, StringComparison.Ordinal))
                {
                    await ExecAsync(conn, sql, ct);
                    await RecordAsync(conn, name, transaction: null, ct);
                }
                else
                {
                    await using var tx = await conn.BeginTransactionAsync(ct);
                    await ExecAsync(conn, sql, ct);
                    await RecordAsync(conn, name, tx, ct);
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

    private static async Task RecordAsync(
        NpgsqlConnection conn, string name, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO schema_migrations (filename) VALUES ($1)", conn, transaction);
        cmd.Parameters.AddWithValue(name);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
