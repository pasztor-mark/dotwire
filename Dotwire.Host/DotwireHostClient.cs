using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dotwire.Host;

/// <summary>
/// Host-side SDK: mints RS256 tokens with the host's private key and talks to dotwire over
/// HTTP. See <see cref="IDotwireHostClient"/> for the full surface and hook semantics.
/// </summary>
public sealed partial class DotwireHostClient : IDotwireHostClient
{
    private readonly HttpClient _http;
    private readonly DotwireTokenSigner _signer;
    private readonly DotwireHostOptions _options;

    private readonly CachedToken _adminToken;
    private readonly CachedToken _auditorToken;

    /// <summary>(userId) -> resolved role, cached for <see cref="DotwireHostOptions.RoleCacheTtl"/>.</summary>
    private readonly ConcurrentDictionary<string, (string Role, DateTimeOffset ExpiresAt)> _roleCache = new();

    public DotwireHostClient(HttpClient http, DotwireTokenSigner signer, DotwireHostOptions options)
    {
        _http = http;
        _signer = signer;
        _options = options;
        _adminToken = new CachedToken(() => _signer.MintToken(_options.AdminUserId, "admin", ttl: _options.DefaultTokenTtl), _options.DefaultTokenTtl);
        _auditorToken = new CachedToken(() => _signer.MintToken(_options.AuditorUserId, "auditor", ttl: _options.DefaultTokenTtl), _options.DefaultTokenTtl);
    }

    /// <inheritdoc />
    public Func<PresendContext, CancellationToken, Task<PresendResult>>? Presend { get; set; }

    /// <inheritdoc />
    public Func<PostSendContext, CancellationToken, Task>? PostSend { get; set; }

    /// <inheritdoc />
    public string MintToken(string userId, string? role = null, string? displayName = null, TimeSpan? ttl = null) =>
        _signer.MintToken(userId, role ?? "member", displayName, ttl);

    /// <inheritdoc />
    public DotwireRoom Room(Guid roomId) => new(this, roomId);

    private string AdminToken() => _adminToken.Get();

    private string AuditorToken() => _auditorToken.Get();

    /// <summary>
    /// Wraps a per-user token mint with the resolution rule (spec §4.3): try `member` first;
    /// on 403 look the role up via the admin API, cache it, retry once with the resolved role.
    /// A second 403 surfaces as-is (<see cref="DotwireApiException.Kind"/> == Forbidden).
    /// </summary>
    private async Task<T> WithResolvedRoleAsync<T>(string userId, Func<string, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        var role = _roleCache.TryGetValue(userId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow
            ? cached.Role
            : "member";

        try
        {
            return await attempt(role, ct);
        }
        catch (DotwireApiException ex) when (ex.Kind == DotwireErrorKind.Forbidden && role == "member")
        {
            var resolved = await GetUserRoleAsync(userId, ct);
            if (resolved is null)
                throw;

            _roleCache[userId] = (resolved.Role, DateTimeOffset.UtcNow.Add(_options.RoleCacheTtl));
            return await attempt(resolved.Role, ct);
        }
    }

    // ---- low-level HTTP -----------------------------------------------------

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string bearerToken, HttpContent? content, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new DotwireApiException(method, path, HttpStatusCode.ServiceUnavailable, "request_timeout");
        }

        if (response.IsSuccessStatusCode)
            return response;

        string? errorCode = null;
        string? reason = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync(DotwireHostJsonContext.Default.ApiErrorBody, ct);
            errorCode = body?.Error;
            reason = body?.Reason;
        }
        catch
        {
            // Bare 401/403/404 responses carry no body - that's expected, not an error.
        }

        TimeSpan? retryAfter = null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is { } ra)
        {
            retryAfter = ra.Delta ?? (ra.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        }

        response.Dispose();
        throw new DotwireApiException(method, path, response.StatusCode, errorCode, reason, retryAfter);
    }

    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method, string path, string bearerToken, JsonTypeInfo<TResponse> responseType, CancellationToken ct, HttpContent? content = null)
    {
        using var response = await SendRawAsync(method, path, bearerToken, content, ct);
        return await response.Content.ReadFromJsonAsync(responseType, ct)
               ?? throw new DotwireApiException(method, path, response.StatusCode, "empty_response");
    }

    private async Task SendAsync(HttpMethod method, string path, string bearerToken, CancellationToken ct, HttpContent? content = null)
    {
        using var response = await SendRawAsync(method, path, bearerToken, content, ct);
    }

    private static HttpContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo) => JsonContent.Create(value, typeInfo);

    /// <summary>Lazily mints, then caches a token, re-minting one minute before its <paramref name="ttl"/> expiry.</summary>
    private sealed class CachedToken(Func<string> mint, TimeSpan ttl)
    {
        private readonly object _gate = new();
        private readonly TimeSpan _renewMargin = ttl > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : ttl / 2;
        private string? _token;
        private DateTimeOffset _expiresAt;

        public string Get()
        {
            lock (_gate)
            {
                if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                    return _token;

                _token = mint();
                _expiresAt = DateTimeOffset.UtcNow.Add(ttl) - _renewMargin;
                return _token;
            }
        }
    }
}
