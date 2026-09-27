namespace Dotwire.Configuration;

/// <summary>Startup retention wiring (spec §3.11). Manage retention in one place - config or
/// the admin API/SDK - not both: unset means leave whatever the API last set.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Dotwire:Retention";

    public int? MessagesDays { get; set; }
}
