using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Nats;

/// <summary>Outcome of a presend webhook call (spec §3.8), including "no webhook configured".</summary>
public enum PresendOutcome
{
    /// <summary>No `Presend:Url` configured - the caller should proceed unmodified.</summary>
    NotConfigured,
    Allowed,
    AllowedWithRewrite,
    Rejected,
    Unavailable,
}

public sealed record PresendVerdict(PresendOutcome Outcome, string? Content = null, string? Reason = null);

/// <summary>
/// Calls the operator's presend webhook (spec §3.8) before a message is encrypted and
/// published. One shared, pooled <see cref="HttpClient"/> (via <see cref="IHttpClientFactory"/>)
/// for both presend and postsend.
/// </summary>
public sealed class PresendWebhookClient(IHttpClientFactory httpClientFactory, IOptions<WebhooksOptions> options, ILogger<PresendWebhookClient> logger)
{
    public const string HttpClientName = "dotwire-webhooks";

    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.Presend.Url);

    public async Task<PresendVerdict> EvaluateAsync(Guid roomId, string senderId, string content, DateTimeOffset time, string source, CancellationToken ct)
    {
        var presend = options.Value.Presend;
        if (string.IsNullOrEmpty(presend.Url))
            return new PresendVerdict(PresendOutcome.NotConfigured);

        var request = new PresendWebhookRequest(roomId, senderId, content, time, source);
        var body = JsonSerializer.SerializeToUtf8Bytes(request, DotwireJsonContext.Default.PresendWebhookRequest);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(presend.TimeoutMs));

            using var httpRequest = BuildRequest(presend.Url, body, "presend");
            SignIfConfigured(httpRequest, body, options.Value.Secret);
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(httpRequest, cts.Token);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return Unavailable(presend, "non-200 response from presend webhook", content);

            var responseBody = await response.Content.ReadFromJsonAsync(
                DotwireJsonContext.Default.PresendWebhookResponse, cts.Token);
            if (responseBody is null)
                return Unavailable(presend, "unparseable presend webhook response", content);

            if (!responseBody.Allow)
                return new PresendVerdict(PresendOutcome.Rejected, Reason: responseBody.Reason);

            if (responseBody.Content is not null && responseBody.Content != content)
                return new PresendVerdict(PresendOutcome.AllowedWithRewrite, Content: responseBody.Content);

            return new PresendVerdict(PresendOutcome.Allowed, Content: content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Presend webhook call failed: {Error}", ex.Message);
            return Unavailable(presend, ex.Message, content);
        }
    }

    private PresendVerdict Unavailable(PresendOptions presend, string reason, string originalContent)
    {
        if (presend.FailPolicy == PresendFailPolicy.Open)
        {
            logger.LogWarning("Presend webhook unavailable ({Reason}); FailPolicy=Open, publishing original content", reason);
            return new PresendVerdict(PresendOutcome.Allowed, Content: originalContent);
        }

        return new PresendVerdict(PresendOutcome.Unavailable, Reason: reason);
    }

    internal static HttpRequestMessage BuildRequest(string url, byte[] body, string eventName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Dotwire-Event", eventName);
        return request;
    }

    internal static void SignIfConfigured(HttpRequestMessage request, byte[] body, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return;

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        request.Headers.Add("X-Dotwire-Signature", $"sha256={Convert.ToHexStringLower(hash)}");
    }
}
