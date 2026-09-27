namespace Dotwire.Configuration;

/// <summary>In-process token-bucket rate limits, per node (spec §3.10). With N API nodes the
/// effective limit is N×, documented rather than coordinated.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Dotwire:RateLimits";

    public bool Enabled { get; set; } = true;

    /// <summary>Per user (sub); member send endpoint.</summary>
    public RateLimitBucketOptions Send { get; set; } = new() { PermitLimit = 20, TokensPerPeriod = 5, PeriodSeconds = 1 };

    /// <summary>Per user; history, SSE connect, /audit routes.</summary>
    public RateLimitBucketOptions Read { get; set; } = new() { PermitLimit = 30, TokensPerPeriod = 10, PeriodSeconds = 1 };

    /// <summary>Per user; every /admin route, DSAR and retention included.</summary>
    public RateLimitBucketOptions Admin { get; set; } = new() { PermitLimit = 200, TokensPerPeriod = 50, PeriodSeconds = 1 };

    /// <summary>Per connection; hub Subscribe/Unsubscribe. Typing keeps its own cooldown, unaffected.</summary>
    public RateLimitBucketOptions Hub { get; set; } = new() { PermitLimit = 30, TokensPerPeriod = 10, PeriodSeconds = 1 };
}

public sealed class RateLimitBucketOptions
{
    public int PermitLimit { get; set; }
    public int TokensPerPeriod { get; set; }
    public int PeriodSeconds { get; set; }
}
