using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    private const string SignaturePrefix = "sha256=";

    public async Task<ClientSendResult> HandleClientSendAsync(Stream body, string senderUserId, CancellationToken ct = default)
    {
        var bytes = await ReadBoundedAsync(body, _options.MaxWebhookBodyBytes, ct);
        if (bytes is null)
            return new ClientSendResult(413, Serialize(new ApiErrorBody("payload_too_large")), null, "payload_too_large", null);

        ClientSendRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(bytes, DotwireHostJsonContext.Default.ClientSendRequest);
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
            var ack = await SendAsUserAsync(request.RoomId, senderUserId, request.Content, request.IdempotencyKey, ct);
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
            return new ClientSendResult(202, Serialize(ex.Sent, DotwireHostJsonContext.Default.SendMessageResult), ex.Sent, null, null);
        }
    }

    public Task<WebhookResult> HandlePresendWebhookAsync(Stream body, string? signatureHeader, CancellationToken ct = default) =>
        HandlePresendWebhookAsync(body, new WebhookHeaders(signatureHeader), ct);

    public async Task<WebhookResult> HandlePresendWebhookAsync(Stream body, WebhookHeaders headers, CancellationToken ct = default)
    {
        EnsureWebhookSecretConfigured();
        var bytes = await ReadBoundedAsync(body, _options.MaxWebhookBodyBytes, ct);
        if (bytes is null)
            return PayloadTooLarge();
        if (!VerifySignature(bytes, headers, DateTimeOffset.UtcNow))
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

    public Task<WebhookResult> HandlePostSendWebhookAsync(Stream body, string? signatureHeader, CancellationToken ct = default) =>
        HandlePostSendWebhookAsync(body, new WebhookHeaders(signatureHeader), ct);

    public async Task<WebhookResult> HandlePostSendWebhookAsync(Stream body, WebhookHeaders headers, CancellationToken ct = default)
    {
        EnsureWebhookSecretConfigured();
        var bytes = await ReadBoundedAsync(body, _options.MaxWebhookBodyBytes, ct);
        if (bytes is null)
            return PayloadTooLarge();
        if (!VerifySignature(bytes, headers, DateTimeOffset.UtcNow))
            return Unauthorized();

        var request = JsonSerializer.Deserialize(bytes, DotwireHostJsonContext.Default.PostSendWebhookRequestDto)
            ?? throw new JsonException("empty postsend webhook body");

        if (PostSend is not null)
        {
            try
            {
                await PostSend(new PostSendContext(request.RoomId, request.Seq, request.Time, request.Content, request.SenderId), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        return new WebhookResult(200, "{}");
    }

    private void EnsureWebhookSecretConfigured()
    {
        if (string.IsNullOrEmpty(_options.WebhookSecret) && !_options.AllowUnsignedWebhooks)
            throw new InvalidOperationException(
                "DotwireHostOptions.WebhookSecret is required to handle webhooks; set AllowUnsignedWebhooks = true to accept unsigned calls.");
    }

    internal bool VerifySignature(byte[] body, WebhookHeaders headers, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(_options.WebhookSecret))
            return _options.AllowUnsignedWebhooks;

        var key = Encoding.UTF8.GetBytes(_options.WebhookSecret);

        if (!string.IsNullOrEmpty(headers.SignatureV2) && !string.IsNullOrEmpty(headers.Timestamp))
        {
            if (!long.TryParse(headers.Timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix))
                return false;
            if ((now - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > _options.WebhookTimestampTolerance)
                return false;

            var prefix = Encoding.UTF8.GetBytes(headers.Timestamp + ".");
            var payload = new byte[prefix.Length + body.Length];
            prefix.CopyTo(payload, 0);
            body.CopyTo(payload, prefix.Length);
            return SignatureMatches(key, payload, headers.SignatureV2);
        }

        return _options.AllowLegacyWebhookSignatures && SignatureMatches(key, body, headers.Signature);
    }

    private static bool SignatureMatches(byte[] key, byte[] payload, string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith(SignaturePrefix, StringComparison.Ordinal))
            return false;

        var computedHex = Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(header[SignaturePrefix.Length..]));
    }

    private static WebhookResult Unauthorized() => new(401, Serialize(new ApiErrorBody("invalid_signature")));

    private static WebhookResult PayloadTooLarge() => new(413, Serialize(new ApiErrorBody("payload_too_large")));

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Serialize(ApiErrorBody body) => Serialize(body, DotwireHostJsonContext.Default.ApiErrorBody);

    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);
}
