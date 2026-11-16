using System.Text;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Nats;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Dotwire.Api;

public sealed record SendRejection(string Code, string? Reason = null);

public sealed class MessageSend(
    MessageCipher cipher,
    INatsJSContext js,
    INatsConnection nats,
    IOptions<DotwireOptions> dotwireOptions,
    IOptions<NatsOptions> natsOptions,
    PresendWebhookClient presend,
    PostSendWebhookService postSend,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Dotwire.Api.Messages");

    public async Task<(SendMessageResponse? Response, SendRejection? Rejection)> SendAsync(
        Guid roomId, string senderId, string? content, string? idempotencyKey, CancellationToken ct)
    {
        var maxBytes = dotwireOptions.Value.MaxContentBytes;

        if (Messages.ValidateContent(content, maxBytes) is { } invalid)
            return (null, invalid);

        if (!IdValidation.IsValid(senderId))
            return (null, new SendRejection("invalid_user_id"));

        var hasKey = !string.IsNullOrEmpty(idempotencyKey);
        if (hasKey && (idempotencyKey!.Length > Messages.MaxIdempotencyKeyLength || !IdValidation.IsValid(idempotencyKey)))
            return (null, new SendRejection("invalid_request", "Idempotency-Key must be 1-128 printable characters"));

        var time = DateTimeOffset.UtcNow;

        var verdict = await presend.EvaluateAsync(roomId, senderId, content!, time, "member", ct);
        var (presendError, finalContent) = Messages.ApplyPresend(verdict, content!, maxBytes);
        if (presendError is not null)
            return (null, presendError);

        var plaintext = Encoding.UTF8.GetBytes(finalContent);

        MessagePublisher.PublishResult result;
        try
        {
            result = await MessagePublisher.PublishAsync(
                roomId, senderId, plaintext, cipher, js, nats, natsOptions.Value, _logger, ct,
                hasKey ? $"{roomId:N}:{senderId}:{idempotencyKey}" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("JetStream publish failed for room {RoomId}: {Error}", roomId, ex.Message);
            return (null, new SendRejection("stream_unavailable"));
        }

        if (!result.Duplicate)
            postSend.Enqueue(new PostSendWebhookRequest(roomId, result.Seq, senderId, finalContent, result.Time, "member"));

        return (new SendMessageResponse(result.Seq, result.Time), null);
    }
}
