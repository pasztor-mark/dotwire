using Npgsql;

namespace Dotwire.Data;

/// <summary>
/// Service keys for the two <see cref="Npgsql.NpgsqlDataSource"/> registrations.
/// Reads resolve <see cref="Read"/>, writes resolve <see cref="Write"/> - never cross them
/// (AGENTS.md hard convention; ARCHITECTURE.md, "Data layout").
/// </summary>
public static class PostgresDataSources
{
    public const string Read = "postgres-read";
    public const string Write = "postgres-write";

    public static NpgsqlDataSource Create(string connectionString, bool noResetOnClose = false)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        if (builder.ConnectionStringBuilder.MaxAutoPrepare == 0)
            builder.ConnectionStringBuilder.MaxAutoPrepare = 64;
        if (noResetOnClose)
            builder.ConnectionStringBuilder.NoResetOnClose = true;
        return builder.Build();
    }

    public static async Task<int> WarmAsync(NpgsqlDataSource dataSource, int count, TimeSpan budget, ILogger logger)
    {
        if (count <= 0)
            return 0;

        var held = new List<NpgsqlConnection>(count);
        try
        {
            using var cts = new CancellationTokenSource(budget);
            for (var i = 0; i < count; i++)
                held.Add(await dataSource.OpenConnectionAsync(cts.Token));
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException)
        {
            logger.LogWarning("Postgres read pool warm-up stopped after {Opened} of {Count} connections: {Message}", held.Count, count, ex.Message);
        }
        finally
        {
            foreach (var connection in held)
                await connection.DisposeAsync();
        }

        return held.Count;
    }
}
