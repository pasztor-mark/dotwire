namespace Dotwire.Configuration;

/// <summary>Presend/postsend webhook configuration (spec §3.8).</summary>
public sealed class WebhooksOptions
{
    public const string SectionName = "Dotwire:Webhooks";

    /// <summary>When set, every webhook call carries `X-Dotwire-Signature: sha256=&lt;hex HMAC-SHA256 of the raw body&gt;`.</summary>
    public string? Secret { get; set; }

    public PresendOptions Presend { get; set; } = new();

    public PostSendOptions PostSend { get; set; } = new();
}

public enum PresendFailPolicy
{
    /// <summary>An unreachable/erroring presend webhook fails the send (503 presend_unavailable).</summary>
    Closed,

    /// <summary>An unreachable/erroring presend webhook lets the original content through.</summary>
    Open,
}

public sealed class PresendOptions
{
    /// <summary>Presend is on only when this is set.</summary>
    public string? Url { get; set; }

    public int TimeoutMs { get; set; } = 500;

    public PresendFailPolicy FailPolicy { get; set; } = PresendFailPolicy.Closed;

    /// <summary>Whether presend also runs for admin message injection (spec §3.3).</summary>
    public bool IncludeAdminSends { get; set; } = false;
}

public sealed class PostSendOptions
{
    /// <summary>Postsend is on only when this is set.</summary>
    public string? Url { get; set; }

    public int TimeoutMs { get; set; } = 2000;

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Full queue drops the newest item with a warning; postsend never blocks the send path.</summary>
    public int QueueCapacity { get; set; } = 10000;

    public bool IncludeAdminSends { get; set; } = false;
}
