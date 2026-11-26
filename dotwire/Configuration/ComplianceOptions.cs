namespace Dotwire.Configuration;

public sealed class ComplianceOptions
{
    public const string SectionName = "Dotwire:Compliance";

    public bool AllowChanges { get; set; } = true;

    public int MinimumKeepDays { get; set; } = 0;

    public ErasureOptions Erasure { get; set; } = new();

    public int HoldRetentionWaitSeconds { get; set; } = 30;
}

public sealed class ErasureOptions
{
    public ErasureMode Mode { get; set; } = ErasureMode.Anonymize;

    public int RecheckMinutes { get; set; } = 60;
}

public enum ErasureMode
{
    Anonymize = 1,
    Delete = 2,
}
