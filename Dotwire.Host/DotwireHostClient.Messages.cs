using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    /// <inheritdoc />
    public Task<SendMessageResult> SendAsUserAsync(Guid roomId, string userId, string content, CancellationToken ct = default) =>
        SendAsUserAsync(roomId, userId, content, null, ct);

    public async Task<SendMessageResult> SendAsUserAsync(Guid roomId, string userId, string content, string? idempotencyKey, CancellationToken ct = default)
    {
        if (Presend is not null)
        {
            var presendResult = await Presend(new PresendContext(roomId, content, userId), ct);
            if (!presendResult.Allow)
                throw new PresendRejectedException(presendResult.RejectionReason ?? "Message rejected by presend hook.");
            if (presendResult.Content is not null)
                content = presendResult.Content;
        }

        var finalContent = content;
        var ack = await WithResolvedRoleAsync(userId, (role, innerCt) =>
        {
            var token = MintMemberCallToken(userId, role);
            var body = JsonBody(new SendMessageRequestDto(finalContent), DotwireHostJsonContext.Default.SendMessageRequestDto);
            return SendAsync(HttpMethod.Post, $"/rooms/{roomId}/messages", token, DotwireHostJsonContext.Default.SendMessageResult, innerCt, body, idempotencyKey);
        }, ct);

        if (PostSend is not null)
        {
            try
            {
                await PostSend(new PostSendContext(roomId, ack.Seq, ack.Time, finalContent, userId), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PostSendException(ack, ex);
            }
        }

        return ack;
    }

    /// <inheritdoc />
    public Task<SendMessageResult> SendSystemMessageAsync(Guid roomId, string content, string senderId = "system", CancellationToken ct = default)
    {
        var body = JsonBody(new InjectMessageRequestDto(content, senderId), DotwireHostJsonContext.Default.InjectMessageRequestDto);
        return SendAsync(HttpMethod.Post, $"/admin/rooms/{roomId}/messages", AdminToken(), DotwireHostJsonContext.Default.SendMessageResult, ct, body);
    }

    /// <inheritdoc />
    public Task<RoomHistory> GetMessagesAsync(Guid roomId, string asUserId, HistoryQuery? query = null, CancellationToken ct = default)
    {
        var qs = BuildHistoryQuery(query);
        return WithResolvedRoleAsync(asUserId, (role, innerCt) =>
        {
            var token = MintMemberCallToken(asUserId, role);
            return SendAsync(HttpMethod.Get, $"/rooms/{roomId}/messages{qs}", token, DotwireHostJsonContext.Default.RoomHistory, innerCt);
        }, ct);
    }

    public async Task<long> ExportRoomHistoryAsync(Guid roomId, string asUserId, Stream destination, CancellationToken ct = default)
    {
        ulong afterSeq = 0;
        long count = 0;
        while (true)
        {
            var history = await GetMessagesAsync(roomId, asUserId, new HistoryQuery(AfterSeq: afterSeq, Limit: 100), ct);
            foreach (var message in history.Messages)
            {
                await WriteLineAsync(destination, message, DotwireHostJsonContext.Default.HistoryMessage, ct);
                count++;
            }
            if (!history.HasMore)
                return count;
            if (history.Messages.Count == 0)
                throw new InvalidDataException($"Room history page after seq {afterSeq} returned no readable messages.");
            afterSeq = history.Messages[^1].Seq;
        }
    }

    private static async Task WriteLineAsync<T>(Stream destination, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(destination, value, typeInfo, ct);
        await destination.WriteAsync(NewLine, ct);
    }

    /// <inheritdoc />
    public Task<RedactMessageResult> RedactMessageAsync(Guid roomId, ulong seq, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/admin/rooms/{roomId}/messages/{seq}", AdminToken(), DotwireHostJsonContext.Default.RedactMessageResult, ct);

    public Task<RedactMessageResult> EmergencyDeleteMessageAsync(Guid roomId, ulong seq, EmergencyDeletionReason reason, CancellationToken ct = default)
    {
        var body = JsonBody(new EmergencyDeleteRequestDto(EmergencyReasonWire(reason)), DotwireHostJsonContext.Default.EmergencyDeleteRequestDto);
        return SendAsync(HttpMethod.Post, $"/admin/rooms/{roomId}/messages/{seq}/emergency-delete", AdminToken(),
            DotwireHostJsonContext.Default.RedactMessageResult, ct, body);
    }

    private static string EmergencyReasonWire(EmergencyDeletionReason reason) => reason switch
    {
        EmergencyDeletionReason.IllegalContent => "illegal_content",
        EmergencyDeletionReason.CourtOrder => "court_order",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string BuildHistoryQuery(HistoryQuery? query)
    {
        if (query is null)
            return string.Empty;

        var parts = new List<string>();
        if (query.AfterSeq.HasValue) parts.Add($"afterSeq={query.AfterSeq.Value}");
        if (query.BeforeSeq.HasValue) parts.Add($"beforeSeq={query.BeforeSeq.Value}");
        if (query.Limit.HasValue) parts.Add($"limit={query.Limit.Value}");

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
