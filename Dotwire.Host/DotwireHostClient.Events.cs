using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    /// <inheritdoc />
    public async IAsyncEnumerable<RoomEvent> StreamEventsAsync(
        Guid roomId,
        string asUserId,
        StreamOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new StreamOptions();
        ulong? lastSeq = options.AfterSeq;
        var role = _roleCache.TryGetValue(asUserId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow
            ? cached.Role
            : "member";
        var backoff = TimeSpan.FromSeconds(1);

        while (true)
        {
            HttpResponseMessage? response = null;
            Exception? connectFailure = null;

            try
            {
                (response, role) = await ConnectSseAsync(roomId, asUserId, role, lastSeq, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                connectFailure = ex;
            }

            if (connectFailure is not null)
            {
                if (!options.AutoResume)
                    throw connectFailure;

                await Task.Delay(backoff, ct);
                backoff = NextBackoff(backoff);
                continue;
            }

            var revoked = false;
            using (response)
            {
                var stream = await response!.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? eventName = null;
                var data = new StringBuilder();

                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        break; // connection dropped mid-stream
                    }

                    if (line is null)
                        break; // EOF - connection dropped

                    if (line.Length == 0)
                    {
                        if (eventName is not null)
                        {
                            var evt = ParseEvent(roomId, eventName, data.ToString());
                            eventName = null;
                            data.Clear();

                            if (evt is not null)
                            {
                                if (evt is MessageEvent msg)
                                    lastSeq = msg.Seq;

                                yield return evt;

                                if (evt is RevokedEvent)
                                {
                                    revoked = true;
                                    break;
                                }
                            }
                        }
                        continue;
                    }

                    if (line.StartsWith(':'))
                        continue; // comment / keep-alive ping

                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                        eventName = line["event: ".Length..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal))
                        data.Append(line["data: ".Length..]);
                    // "id: " lines are ignored here - each payload already carries its own seq.
                }
            }

            if (revoked || !options.AutoResume)
                yield break;

            await Task.Delay(backoff, ct);
            backoff = NextBackoff(backoff);
        }
    }

    private static TimeSpan NextBackoff(TimeSpan current)
    {
        var next = current.TotalSeconds * 2;
        return TimeSpan.FromSeconds(Math.Min(next, 30));
    }

    private async Task<(HttpResponseMessage Response, string Role)> ConnectSseAsync(
        Guid roomId, string userId, string startRole, ulong? afterSeq, CancellationToken ct)
    {
        var path = $"/rooms/{roomId}/events";
        var role = startRole;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = MintToken(userId, role, ttl: _options.DefaultTokenTtl);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (afterSeq.HasValue)
                request.Headers.TryAddWithoutValidation("Last-Event-ID", afterSeq.Value.ToString());

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
                return (response, role);

            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && role == "member")
            {
                response.Dispose();
                var resolved = await GetUserRoleAsync(userId, ct);
                if (resolved is null)
                    throw new DotwireApiException(HttpMethod.Get, path, HttpStatusCode.Forbidden);

                role = resolved.Role;
                _roleCache[userId] = (role, DateTimeOffset.UtcNow.Add(_options.RoleCacheTtl));
                continue;
            }

            response.Dispose();
            throw new DotwireApiException(HttpMethod.Get, path, response.StatusCode);
        }

        throw new DotwireApiException(HttpMethod.Get, path, HttpStatusCode.Forbidden);
    }

    private static RoomEvent? ParseEvent(Guid roomId, string eventName, string json) => eventName switch
    {
        "message" => ParseMessage(roomId, json),
        "typing" => ParseTyping(roomId, json),
        "presence" => ParsePresence(roomId, json),
        "retracted" => ParseRetracted(roomId, json),
        "revoked" => new RevokedEvent { RoomId = roomId },
        _ => null,
    };

    private static RoomEvent? ParseMessage(Guid roomId, string json)
    {
        var payload = JsonSerializer.Deserialize(json, DotwireHostJsonContext.Default.SseMessagePayloadDto);
        return payload is null ? null : new MessageEvent
        {
            RoomId = roomId,
            Seq = payload.Seq,
            SenderId = payload.SenderId,
            Time = payload.Time,
            Content = payload.Content,
            Replayed = payload.Replayed,
        };
    }

    private static RoomEvent? ParseTyping(Guid roomId, string json)
    {
        var payload = JsonSerializer.Deserialize(json, DotwireHostJsonContext.Default.TypingNotificationDto);
        return payload is null ? null : new TypingEvent { RoomId = roomId, UserId = payload.UserId, IsTyping = payload.IsTyping };
    }

    private static RoomEvent? ParsePresence(Guid roomId, string json)
    {
        var payload = JsonSerializer.Deserialize(json, DotwireHostJsonContext.Default.PresenceDeltaDto);
        return payload is null ? null : new PresenceEvent { RoomId = roomId, Joined = payload.Joined, Left = payload.Left };
    }

    private static RoomEvent? ParseRetracted(Guid roomId, string json)
    {
        var payload = JsonSerializer.Deserialize(json, DotwireHostJsonContext.Default.MessageRetractedDto);
        return payload is null ? null : new RetractedEvent { RoomId = roomId, Seq = payload.Seq };
    }
}
