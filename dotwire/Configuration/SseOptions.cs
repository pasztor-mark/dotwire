namespace Dotwire.Configuration;

/// <summary>SSE read-path configuration (spec §3.9).</summary>
public sealed class SseOptions
{
    public const string SectionName = "Dotwire:Sse";

    public bool Enabled { get; set; } = true;

    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>Bounded per-connection channel capacity; a full channel disconnects the laggard.</summary>
    public int BufferSize { get; set; } = 256;

    /// <summary>Rows per page while replaying history on resume.</summary>
    public int ReplayPageSize { get; set; } = 100;
}
