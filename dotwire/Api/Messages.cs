using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dotwire.Api;

/// <summary>Request body for POST /rooms/{roomId}/messages.</summary>
public sealed record SendMessageRequest(string? Content);

/// <summary>The ack contract: seq/time from the JetStream ack, never from Postgres.</summary>
public sealed record SendMessageResponse(ulong Seq, DateTimeOffset Time);

public sealed record RoomMessage(Guid RoomId, string SenderId, DateTimeOffset Time, string KeyId, byte[] Content);

public sealed record RoomMessageDelivery(Guid RoomId, string SenderId, DateTimeOffset Time, string Content);

public sealed record TypingNotification(Guid RoomId, string UserId, bool IsTyping);

public sealed record PresenceDelta(Guid RoomId, string[] Joined, string[] Left);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(SendMessageResponse))]
[JsonSerializable(typeof(RoomMessage))]
[JsonSerializable(typeof(RoomMessageDelivery))]
[JsonSerializable(typeof(TypingNotification))]
[JsonSerializable(typeof(PresenceDelta))]
public partial class DotwireJsonContext : JsonSerializerContext;

public static class Messages
{
    public static void MapMessageEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/rooms/{roomId}/messages")
            .RequireAuthorization()
            .AddEndpointFilter<RoleCrossCheckFilter>()   // AUTH.md stage 3
            .AddEndpointFilter<RoomMembershipFilter>();  // AUTH.md stage 4

        group.MapPost("/", async (
            string roomId,
            SendMessageRequest request,
            HttpContext http,
            MessageCipher cipher,
            INatsJSContext js,
            IOptions<DotwireOptions> dotwireOptions,
            IOptions<NatsOptions> natsOptions,
            ILoggerFactory loggerFactory) =>
        {
            var roomGuid = Guid.Parse(roomId); // RoomMembershipFilter already 404'd non-uuids

            if (string.IsNullOrEmpty(request.Content))
                return Results.BadRequest();
            var plaintext = Encoding.UTF8.GetBytes(request.Content);
            if (plaintext.Length > dotwireOptions.Value.MaxContentBytes)
                return Results.BadRequest();

            var senderId = http.User.FindFirstValue("sub")!;
            var time = DateTimeOffset.UtcNow;
            var message = new RoomMessage(roomGuid, senderId, time, cipher.ActiveKeyId, cipher.Encrypt(plaintext));
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, DotwireJsonContext.Default.RoomMessage);

            try
            {
                var ack = await js.PublishAsync<byte[]>(
                    $"{natsOptions.Value.RoomSubjectPrefix}{roomGuid}",
                    payload,
                    cancellationToken: http.RequestAborted);
                if (ack.Error is not null)
                    throw new InvalidOperationException($"JetStream publish error {ack.Error.Code}");

                // The JetStream persistence ack IS the client ack (ARCHITECTURE.md,
                // "What sent means") - Postgres is never awaited here.
                return Results.Accepted(value: new SendMessageResponse(ack.Seq, time));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ids only - never content, plaintext or encrypted (AGENTS.md hard rule).
                loggerFactory.CreateLogger("Dotwire.Api.Messages").LogError(
                    "JetStream publish failed for room {RoomId}: {Error}", roomGuid, ex.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
