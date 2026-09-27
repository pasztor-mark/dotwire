using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    private const string SignaturePrefix = "sha256=";

    /// <inheritdoc />
    public async Task<ClientSendResult> HandleClientSendAsync(Stream body, string senderUserId, CancellationToken ct = default)
    {
        ClientSendRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(body, DotwireHostJsonContext.Default.ClientSendRequest, ct);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrEmpty(request.Content))
        {
            const string reason = "invalid_content";
            return new ClientSendResult(400, Serialize(new ApiErrorBody(reason)), null, reason, null);
        }

        try
        {
            var ack = await SendAsUserAsync(request.RoomId, senderUserId, request.Content, ct);
            return new ClientSendResult(202, Serialize(ack, DotwireHostJsonContext.Default.SendMessageResult), ack, null, null);
        }
        catch (PresendRejectedException ex)
        {
            return new ClientSendResult(422, Serialize(new ApiErrorBody("presend_rejected", ex.Reason)), null, "presend_rejected", ex.Reason);
        }
        catch (DotwireApiException ex)
        {
            var status = (int)ex.StatusCode;
            return new ClientSendResult(status, Serialize(new ApiErrorBody(ex.ErrorCode ?? "unknown_error", ex.Reason)), null, ex.ErrorCode, ex.Reason);
        }
        catch (PostSendException ex)
        {
            // The send itself succeeded; only the (in-process) PostSend hook failed. The
            // client already has its message accepted - surface the ack, not an error.
            return new ClientSendResult(202, Serialize(ex.Sent, DotwireHostJsonContext.Default.SendMessageResult), ex.Sent, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<WebhookResult> HandlePresendWebhookAsync(Stream body, string? signatureHeader, CancellationToken ct = default)
    {
        var bytes = await ReadAllAsync(body, ct);
        if (!VerifySignature(bytes, signatureHeader))
            return Unauthorized();

        var request = JsonSerializer.Deserialize(bytes, DotwireHostJsonContext.Default.PresendWebhookRequestDto)
            ?? throw new JsonException("empty presend webhook body");

        PresendWebhookResponseDto response;
        if (Presend is null)
        {
            response = new PresendWebhookResponseDto(true);
        }
        else
        {
            var result = await Presend(new PresendContext(request.RoomId, request.Content, request.SenderId), ct);
            response = result.Allow
                ? new PresendWebhookResponseDto(true, result.Content)
                : new PresendWebhookResponseDto(false, Reason: result.RejectionReason);
        }

        return new WebhookResult(200, Serialize(response, DotwireHostJsonContext.Default.PresendWebhookResponseDto));
    }

    /// <inheritdoc />
    public async Task<WebhookResult> HandlePostSendWebhookAsync(Stream body, string? signatureHeader, CancellationToken ct = default)
    {
        var bytes = await ReadAllAsync(body, ct);
        if (!VerifySignature(bytes, signatureHeader))
            return Unauthorized();

        var request = JsonSerializer.Deserialize(bytes, DotwireHostJsonContext.Default.PostSendWebhookRequestDto)
            ?? throw new JsonException("empty postsend webhook body");

        if (PostSend is not null)
        {
            // Best-effort, like the server's own postsend contract: never turns into a
            // non-200 response, since the send it describes already happened either way.
            try
            {
                await PostSend(new PostSendContext(request.RoomId, request.Seq, request.Time, request.Content, request.SenderId), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex; // swallowed: caller can inspect via their own PostSend hook's own logging.
            }
        }

        return new WebhookResult(200, "{}");
    }

    private bool VerifySignature(byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(_options.WebhookSecret))
            return true;

        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
            return false;

        var providedHex = signatureHeader[SignaturePrefix.Length..];
        var key = Encoding.UTF8.GetBytes(_options.WebhookSecret);
        var computed = HMACSHA256.HashData(key, body);
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(providedHex));
    }

    private static WebhookResult Unauthorized() => new(401, Serialize(new ApiErrorBody("invalid_signature")));

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static string Serialize(ApiErrorBody body) => Serialize(body, DotwireHostJsonContext.Default.ApiErrorBody);

    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);
}
