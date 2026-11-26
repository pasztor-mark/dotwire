namespace Dotwire.Configuration;

public static class ComplianceRules
{
    public static void Validate(ComplianceOptions compliance, RetentionOptions retention)
    {
        if (compliance.MinimumKeepDays is < 0 or > 36500)
            throw new InvalidOperationException($"Dotwire:Compliance:MinimumKeepDays must be in the range 0..36500; got {compliance.MinimumKeepDays}.");
        if (compliance.Erasure.RecheckMinutes is < 1 or > 1440)
            throw new InvalidOperationException($"Dotwire:Compliance:Erasure:RecheckMinutes must be in the range 1..1440; got {compliance.Erasure.RecheckMinutes}.");
        if (compliance.HoldRetentionWaitSeconds is < 0 or > 600)
            throw new InvalidOperationException($"Dotwire:Compliance:HoldRetentionWaitSeconds must be in the range 0..600; got {compliance.HoldRetentionWaitSeconds}.");
        if (retention.MessagesDays is { } days && days > 0 && days < compliance.MinimumKeepDays)
            throw new InvalidOperationException(
                $"Dotwire:Retention:MessagesDays ({days}) must be 0 or at least Dotwire:Compliance:MinimumKeepDays ({compliance.MinimumKeepDays}).");
    }
}

public static class ErasureModes
{
    public static string ToWire(ErasureMode mode) => mode switch
    {
        ErasureMode.Anonymize => "anonymize",
        ErasureMode.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static bool TryParse(string? value, out ErasureMode mode)
    {
        switch (value?.ToLowerInvariant())
        {
            case "anonymize":
                mode = ErasureMode.Anonymize;
                return true;
            case "delete":
                mode = ErasureMode.Delete;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
