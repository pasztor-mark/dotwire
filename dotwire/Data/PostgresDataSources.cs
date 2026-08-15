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
}
