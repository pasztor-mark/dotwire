namespace Dotwire.Configuration;

/// <summary>
/// Postgres runtime settings. Connection strings live in the standard
/// <c>ConnectionStrings</c> section (<c>PostgresMigrator</c> / <c>PostgresWrite</c> /
/// <c>PostgresRead</c>). see ARCHITECTURE.md, "Data layout" for the read/write split.
/// </summary>
public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    /// <summary>Run embedded SQL migrations at startup. Tests turn this off.</summary>
    public bool Migrate { get; set; } = true;

    /// <summary>
    /// Password for the <c>dotwire_app</c> runtime role. Migration 0001 creates the role;
    /// the runner sets/refreshes this password every startup (ALTER ROLE can't take a bind
    /// parameter, and the SQL files must not embed secrets).
    /// </summary>
    public string? AppRolePassword { get; set; }
}
