using System.Security.Cryptography;
using Dotwire.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Dotwire.Auth;

/// <summary>
/// Verification-key source for AUTH.md stage 1: merges inline PEM keys with a cached
/// JWKS document. The JWKS cache refreshes on an interval and . rate-limited . when an
/// unrecognized kid shows up (rotation propagation). Fetch failures keep the last good
/// key set: losing the JWKS endpoint must not immediately log every user out.
/// The resolver contract is synchronous, so the rare refresh blocks; steady-state
/// requests hit only the cached array.
/// </summary>
public sealed class SigningKeyRing
{
    private static readonly TimeSpan UnknownKidRefreshFloor = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<SecurityKey> _inlineKeys;
    private readonly string? _jwksUrl;
    private readonly TimeSpan _refreshInterval;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private volatile IReadOnlyList<SecurityKey> _jwksKeys = [];

    // UTC ticks of the last JWKS fetch attempt (successful or not). A plain DateTimeOffset
    // field here would be a multi-field struct read outside the lock in Resolve while
    // written inside the lock in Refresh . a torn read is possible. Ticks is a single
    // long, so Interlocked.Read/Volatile.Write give it an atomic, race-free read/write pair.
    private long _lastFetchTicks;

    public SigningKeyRing(AuthOptions options, HttpClient? http = null, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(options.Issuer) || string.IsNullOrEmpty(options.Audience))
            throw new InvalidOperationException("Auth:Issuer and Auth:Audience must be configured.");
        if (options.Keys.Count == 0 && string.IsNullOrEmpty(options.JwksUrl))
            throw new InvalidOperationException(
                "Configure at least one verification key source: Auth:Keys (inline PEM) or Auth:JwksUrl.");

        var inline = new List<SecurityKey>(options.Keys.Count);
        foreach (var (kid, pem) in options.Keys)
        {
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (ArgumentException ex)
            {
                rsa.Dispose();
                throw new InvalidOperationException($"Auth:Keys:{kid} is not a valid PEM public key.", ex);
            }
            inline.Add(new RsaSecurityKey(rsa) { KeyId = kid });
        }

        _inlineKeys = inline;
        _jwksUrl = options.JwksUrl;
        _refreshInterval = TimeSpan.FromMinutes(options.JwksRefreshMinutes);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _logger = logger;
    }

    public IEnumerable<SecurityKey> Resolve(string? kid)
    {
        // AUTH.md, "Token contract": kid is required in every token's header. A kid-less
        // token must never validate against just any configured key . fail closed.
        if (string.IsNullOrEmpty(kid))
            return [];

        if (_jwksUrl is null)
            return FilterByKid(_inlineKeys, kid);

        var now = DateTimeOffset.UtcNow;
        var jwks = _jwksKeys;
        var lastFetch = new DateTimeOffset(Interlocked.Read(ref _lastFetchTicks), TimeSpan.Zero);
        var stale = now - lastFetch >= _refreshInterval;
        var unknownKid = !jwks.Any(k => k.KeyId == kid)
                         && !_inlineKeys.Any(k => k.KeyId == kid)
                         && now - lastFetch >= UnknownKidRefreshFloor;

        if (stale || unknownKid)
            jwks = Refresh();

        return FilterByKid(_inlineKeys, kid).Concat(FilterByKid(jwks, kid));
    }

    private static IEnumerable<SecurityKey> FilterByKid(IReadOnlyList<SecurityKey> keys, string kid) =>
        keys.Where(k => k.KeyId == kid);

    private IReadOnlyList<SecurityKey> Refresh()
    {
        lock (_gate)
        {
            // Another request may have refreshed while this one waited on the lock.
            var lastFetch = new DateTimeOffset(Interlocked.Read(ref _lastFetchTicks), TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - lastFetch < UnknownKidRefreshFloor)
                return _jwksKeys;

            try
            {
                var json = _http.GetStringAsync(_jwksUrl!).GetAwaiter().GetResult();
                var set = new JsonWebKeySet(json);
                _jwksKeys = set.GetSigningKeys().ToList();
                _logger?.LogInformation("JWKS refreshed: {Count} keys", _jwksKeys.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
            {
                _logger?.LogWarning("JWKS refresh failed, keeping previous keys: {Message}", ex.Message);
            }

            Volatile.Write(ref _lastFetchTicks, DateTimeOffset.UtcNow.UtcTicks);
            return _jwksKeys;
        }
    }
}
