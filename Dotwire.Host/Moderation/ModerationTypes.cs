namespace Dotwire.Host.Moderation;

/// <summary>An accepted message awaiting asynchronous review. (RoomId, Seq) is the redaction target.</summary>
public sealed record PendingModeration(Guid RoomId, ulong Seq, DateTimeOffset Time, string SenderUserId, string Content);

/// <summary>One classifier decision. Only <see cref="Flagged"/> verdicts trigger an action.</summary>
public sealed record ModerationVerdict(Guid RoomId, ulong Seq, bool Flagged, string? Category = null, string? Reason = null);

/// <summary>
/// The expensive judgment call, run off the send path over a whole batch at once. Implementations
/// call an LLM (see <c>samples/Moderation</c> for a Claude Batch API reference implementation),
/// a third-party moderation API, or anything else that is too slow to sit inside a send.
/// Return one verdict per input; inputs with no verdict are treated as not flagged.
/// </summary>
public interface IModerationClassifier
{
    Task<IReadOnlyList<ModerationVerdict>> ClassifyAsync(IReadOnlyList<PendingModeration> batch, CancellationToken ct);
}
