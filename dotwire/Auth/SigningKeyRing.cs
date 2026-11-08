using System.Net;
using System.Security.Cryptography;
using Dotwire.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Dotwire.Auth;

/// <summary>
/// Verification-key source for AUTH.md stage 1: merges inline PEM keys with a cached
/// JWKS document. The JWKS cache refreshes on an interval and . rate-limited . when an
/// unrecognized kid shows up (rotation propagation). Fetch failures keep the last good
/// key set: losing the JWKS endpoint must not immediately log every user out.
/// </summary>
public sealed class SigningKeyRing
{
    public const int MinRsaKeySize = 2048;
    public const int MaxJwksBytes = 256 * 1024;

    private static readonly TimeSpan UnknownKidRefreshFloor = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<SecurityKey> _inlineKeys;
    private readonly string? _jwksUrl;
    private readonly TimeSpan _refreshInterval;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private volatile IReadOnlyList<SecurityKey> _jwksKeys = [];
    private Task _refresh = Task.CompletedTask;

    private long _lastFetchTicks;

    public SigningKeyRing(AuthOptions options, HttpClient? http = null, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(options.Issuer) || string.IsNullOrEmpty(options.Audience))
            throw new InvalidOperationException("Auth:Issuer and Auth:Audience must be configured.");
        if (options.Keys.Count == 0 && string.IsNullOrEmpty(options.JwksUrl))
            throw new InvalidOperationException(
                "Configure at least one verification key source: Auth:Keys (inline PEM) or Auth:JwksUrl.");

        if (!string.IsNullOrEmpty(options.JwksUrl))
        {
            if (!Uri.TryCreate(options.JwksUrl, UriKind.Absolute, out var jwksUri))
                throw new InvalidOperationException("Auth:JwksUrl must be an absolute URL.");
            var secure = jwksUri.Scheme == Uri.UriSchemeHttps
                         || (jwksUri.Scheme == Uri.UriSchemeHttp && IsLoopback(jwksUri));
            if (!secure && !options.AllowInsecureJwksUrl)
                throw new InvalidOperationException(
                    "Auth:JwksUrl must use https (or a loopback host); set Auth:AllowInsecureJwksUrl=true to override.");
        }

        var inline = new List<SecurityKey>(options.Keys.Count);
        foreach (var (kid, pem) in options.Keys)
        {
            if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                throw new InvalidOperationException($"Auth:Keys:{kid} must be a public key, not a private key.");

            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                rsa.Dispose();
                throw new InvalidOperationException($"Auth:Keys:{kid} is not a valid PEM public key.", ex);
            }

            if (rsa.KeySize < MinRsaKeySize)
            {
                rsa.Dispose();
                throw new InvalidOperationException($"Auth:Keys:{kid} must be at least {MinRsaKeySize} bits.");
            }
            inline.Add(new RsaSecurityKey(rsa) { KeyId = kid });
        }

        _inlineKeys = inline;
        _jwksUrl = string.IsNullOrEmpty(options.JwksUrl) ? null : options.JwksUrl;
        _refreshInterval = TimeSpan.FromMinutes(options.JwksRefreshMinutes);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _logger = logger;
    }

    public IEnumerable<SecurityKey> Resolve(string? kid)
    {
        if (string.IsNullOrEmpty(kid))
            return [];

        var inline = FilterByKid(_inlineKeys, kid);
        if (inline.Count > 0 || _jwksUrl is null)
            return inline;

        var now = DateTimeOffset.UtcNow;
        var jwks = _jwksKeys;
        var lastFetch = new DateTimeOffset(Interlocked.Read(ref _lastFetchTicks), TimeSpan.Zero);
        var matches = FilterByKid(jwks, kid);
        var stale = now - lastFetch >= _refreshInterval;
        var unknownKid = matches.Count == 0 && now - lastFetch >= UnknownKidRefreshFloor;

        if (stale || unknownKid)
            _ = StartRefresh(force: false);

        return matches;
    }

    public Task RefreshAsync() => _jwksUrl is null ? Task.CompletedTask : StartRefresh(force: true);

    private static List<SecurityKey> FilterByKid(IReadOnlyList<SecurityKey> keys, string kid) =>
        keys.Where(k => k.KeyId == kid).ToList();

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip));

    private Task StartRefresh(bool force)
    {
        lock (_gate)
        {
            if (!_refresh.IsCompleted)
                return _refresh;

            var lastFetch = new DateTimeOffset(Interlocked.Read(ref _lastFetchTicks), TimeSpan.Zero);
            if (!force && DateTimeOffset.UtcNow - lastFetch < UnknownKidRefreshFloor)
                return _refresh;

            Volatile.Write(ref _lastFetchTicks, DateTimeOffset.UtcNow.UtcTicks);
            return _refresh = FetchAsync();
        }
    }

    private async Task FetchAsync()
    {
        await Task.Yield();
        try
        {
            using var response = await _http.GetAsync(_jwksUrl!, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxJwksBytes)
                throw new InvalidDataException($"JWKS document exceeds {MaxJwksBytes} bytes.");

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxJwksBytes)
                    throw new InvalidDataException($"JWKS document exceeds {MaxJwksBytes} bytes.");
                buffer.Write(chunk, 0, read);
            }

            var set = new JsonWebKeySet(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            var keys = set.GetSigningKeys()
                .Where(k => k is RsaSecurityKey && !string.IsNullOrEmpty(k.KeyId) && k.KeySize >= MinRsaKeySize)
                .ToList();
            if (keys.Count == 0)
                throw new InvalidDataException("JWKS document has no usable RSA signing keys.");

            _jwksKeys = keys;
            _logger?.LogInformation("JWKS refreshed: {Count} keys", keys.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("JWKS refresh failed, keeping previous keys: {Message}", ex.Message);
        }
    }
}
