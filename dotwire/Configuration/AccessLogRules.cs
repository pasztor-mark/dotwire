namespace Dotwire.Configuration;

public static class AccessLogRules
{
    public static void Validate(AccessLogOptions accessLog, NatsOptions nats, AuthOptions auth, ComplianceOptions compliance)
    {
        if (accessLog.RetentionDays is < 0 or > 36500)
            throw new InvalidOperationException($"Dotwire:AccessLog:RetentionDays must be in the range 0..36500; got {accessLog.RetentionDays}.");
        if (accessLog.RetentionDays > 0 && accessLog.RetentionDays < compliance.MinimumKeepDays)
            throw new InvalidOperationException(
                $"Dotwire:AccessLog:RetentionDays ({accessLog.RetentionDays}) must be 0 or at least Dotwire:Compliance:MinimumKeepDays ({compliance.MinimumKeepDays}).");
        if (!accessLog.Enabled)
            return;
        if (!nats.Enabled)
            throw new InvalidOperationException("Dotwire:AccessLog:Enabled requires Nats:Enabled=true; the access log is written through JetStream.");
        if (auth.MaxTokenLifetimeMinutes <= 0)
            throw new InvalidOperationException("Dotwire:AccessLog:Enabled requires Auth:MaxTokenLifetimeMinutes > 0; the token expiry bounds a window whose close was lost.");
    }
}
