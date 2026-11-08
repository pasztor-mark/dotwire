namespace Dotwire.Configuration;

/// <summary>
/// Which parts of the system this process runs. One binary, one flag -
/// see ARCHITECTURE.md, "Realtime and node roles".
/// </summary>
public enum NodeRole
{
    /// <summary>Everything in one process. The single-node self-hosting default.</summary>
    All,

    /// <summary>REST + admin surface only.</summary>
    Api,

    /// <summary>Holds sockets, consumes NATS, nothing else.</summary>
    Gateway,
}

public sealed class DotwireOptions
{
    public const string SectionName = "Dotwire";

    public NodeRole Role { get; set; } = NodeRole.All;

    /// <summary>Max message content size in UTF-8 bytes; oversized sends are 400s.</summary>
    public int MaxContentBytes { get; set; } = 65536;

    public long MaxRequestBodyBytes { get; set; } = 256 * 1024;

    public int MaxConnectionsPerUser { get; set; } = 100;

    public int MembershipSweepSeconds { get; set; } = 60;
}

public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
