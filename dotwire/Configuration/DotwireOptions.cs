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
}
