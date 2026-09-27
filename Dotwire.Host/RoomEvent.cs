namespace Dotwire.Host;

/// <summary>One event yielded by <see cref="IDotwireHostClient.StreamEventsAsync"/>, parsed off the room's SSE stream.</summary>
public abstract record RoomEvent
{
    /// <summary>The room this event belongs to.</summary>
    public required Guid RoomId { get; init; }
}

/// <summary>A message - either a live delivery or a replayed one during resume (see <see cref="Replayed"/>).</summary>
public sealed record MessageEvent : RoomEvent
{
    public required ulong Seq { get; init; }
    public required string SenderId { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required string Content { get; init; }
    public required bool Replayed { get; init; }
}

/// <summary>A message was physically redacted; drop it locally.</summary>
public sealed record RetractedEvent : RoomEvent
{
    public required ulong Seq { get; init; }
}

/// <summary>A user in this room is (or stopped) typing.</summary>
public sealed record TypingEvent : RoomEvent
{
    public required string UserId { get; init; }
    public required bool IsTyping { get; init; }
}

/// <summary>A coalesced presence delta for this room.</summary>
public sealed record PresenceEvent : RoomEvent
{
    public required IReadOnlyList<string> Joined { get; init; }
    public required IReadOnlyList<string> Left { get; init; }
}

/// <summary>
/// This subscriber's membership in the room was revoked; the server is about to close the
/// stream. <see cref="IDotwireHostClient.StreamEventsAsync"/> yields this and then stops -
/// it never auto-reconnects after one, regardless of <see cref="StreamOptions.AutoResume"/>.
/// </summary>
public sealed record RevokedEvent : RoomEvent;

/// <summary>Options for <see cref="IDotwireHostClient.StreamEventsAsync"/>.</summary>
public sealed record StreamOptions
{
    /// <summary>
    /// Reconnect after a dropped connection, resuming with `Last-Event-ID` set to the last
    /// seen message seq, using exponential backoff (1s, capped at 30s) between attempts.
    /// A fresh token is minted for every reconnect attempt. Never reconnects after a
    /// <see cref="RevokedEvent"/> is yielded.
    /// </summary>
    public bool AutoResume { get; init; } = true;

    /// <summary>Resume position: only messages with seq greater than this are replayed. Passed as `Last-Event-ID`/`afterSeq`.</summary>
    public ulong? AfterSeq { get; init; }
}
