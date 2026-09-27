namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    /// <inheritdoc />
    public async Task<SendMessageResult> SendAsUserAsync(Guid roomId, string userId, string content, CancellationToken ct = default)
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
            var token = MintToken(userId, role, ttl: _options.DefaultTokenTtl);
            var body = JsonBody(new SendMessageRequestDto(finalContent), DotwireHostJsonContext.Default.SendMessageRequestDto);
            return SendAsync(HttpMethod.Post, $"/rooms/{roomId}/messages", token, DotwireHostJsonContext.Default.SendMessageResult, innerCt, body);
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
            var token = MintToken(asUserId, role, ttl: _options.DefaultTokenTtl);
            return SendAsync(HttpMethod.Get, $"/rooms/{roomId}/messages{qs}", token, DotwireHostJsonContext.Default.RoomHistory, innerCt);
        }, ct);
    }

    /// <inheritdoc />
    public Task<RedactMessageResult> RedactMessageAsync(Guid roomId, ulong seq, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/admin/rooms/{roomId}/messages/{seq}", AdminToken(), DotwireHostJsonContext.Default.RedactMessageResult, ct);

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
