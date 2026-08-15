using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dotwire.Auth;
using Dotwire.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace dotwire.Tests;

public class SigningKeyRingTests
{
    private static string NewPublicPem(out RSA rsa)
    {
        rsa = RSA.Create(2048);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    private static AuthOptions ValidOptions() => new()
    {
        Issuer = "test-host",
        Audience = "dotwire",
    };

    /// <summary>Hand-built JWKS document (kty/n/e) for one RSA public key, avoiding any
    /// dependency on a particular JsonWebKey serialization helper being available.</summary>
    private static HttpResponseMessage JwksResponse(RSA rsa, string kid)
    {
        var parameters = rsa.ExportParameters(false);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var json = $$"""
            {"keys":[{"kty":"RSA","use":"sig","kid":"{{kid}}","alg":"RS256","n":"{{n}}","e":"{{e}}"}]}
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Sets the ring's private last-fetch timestamp back to the epoch so the next
    /// Resolve call bypasses the real-time 30s unknown-kid floor without sleeping or adding
    /// a clock abstraction to production code, purely a test seam via reflection.</summary>
    private static void RewindLastFetch(SigningKeyRing ring) =>
        typeof(SigningKeyRing)
            .GetField("_lastFetchTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(ring, 0L);

    private sealed class FakeJwksHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond());
        }
    }

    [Fact]
    public void ResolvesInlinePemByKid()
    {
        var options = ValidOptions();
        options.Keys["k-2026"] = NewPublicPem(out _);

        var ring = new SigningKeyRing(options);
        var keys = ring.Resolve("k-2026").ToList();

        Assert.Single(keys);
        Assert.Equal("k-2026", keys[0].KeyId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingKidYieldsNoKeysEvenWithInlineKeysConfigured(string? kid)
    {
        // AUTH.md, "Token contract": kid is required in every token's header. A kid-less
        // token must never validate against just any configured key . fail closed.
        var options = ValidOptions();
        options.Keys["k-2026"] = NewPublicPem(out _);

        var ring = new SigningKeyRing(options);

        Assert.Empty(ring.Resolve(kid));
    }

    [Fact]
    public void UnknownKidYieldsNoKeysWhenInlineOnly()
    {
        var options = ValidOptions();
        options.Keys["k-2026"] = NewPublicPem(out _);

        var ring = new SigningKeyRing(options);

        Assert.Empty(ring.Resolve("other"));
    }

    [Fact]
    public void GarbagePemThrowsAtConstruction()
    {
        var options = ValidOptions();
        options.Keys["bad"] = "not a pem";

        Assert.Throws<InvalidOperationException>(() => new SigningKeyRing(options));
    }

    [Theory]
    [InlineData("", "dotwire")]
    [InlineData("test-host", "")]
    public void MissingIssuerOrAudienceThrows(string issuer, string audience)
    {
        var options = new AuthOptions { Issuer = issuer, Audience = audience };
        options.Keys["k"] = NewPublicPem(out _);

        Assert.Throws<InvalidOperationException>(() => new SigningKeyRing(options));
    }

    [Fact]
    public void NoKeySourceThrows()
    {
        // Neither inline keys nor a JWKS URL: dotwire could never verify anything . fail closed at startup.
        Assert.Throws<InvalidOperationException>(() => new SigningKeyRing(ValidOptions()));
    }

    [Fact]
    public void ResolvesJwksKeyByKidAfterFetch()
    {
        var jwksRsa = RSA.Create(2048);
        const string jwksKid = "jwks-key-1";
        var handler = new FakeJwksHandler(() => JwksResponse(jwksRsa, jwksKid));
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/.well-known/jwks.json";

        var ring = new SigningKeyRing(options, new HttpClient(handler));
        var keys = ring.Resolve(jwksKid).ToList();

        Assert.Single(keys);
        Assert.Equal(jwksKid, keys[0].KeyId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void UnknownKidWithinFloorDoesNotRefetch()
    {
        var jwksRsa = RSA.Create(2048);
        const string jwksKid = "jwks-key-1";
        var handler = new FakeJwksHandler(() => JwksResponse(jwksRsa, jwksKid));
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/.well-known/jwks.json";

        var ring = new SigningKeyRing(options, new HttpClient(handler));

        // First call: cache is empty/stale, so it fetches once regardless of kid.
        ring.Resolve(jwksKid).ToList();
        Assert.Equal(1, handler.CallCount);

        // Second call, immediately after, with an unrecognized kid: still inside the
        // 30s unknown-kid floor (real elapsed time here is milliseconds), so no refetch.
        var keys = ring.Resolve("some-other-kid").ToList();

        Assert.Empty(keys);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void FetchFailureKeepsPreviousKeysAndDoesNotThrow()
    {
        var jwksRsa = RSA.Create(2048);
        const string jwksKid = "jwks-key-1";
        var attempt = 0;
        var handler = new FakeJwksHandler(() =>
        {
            attempt++;
            return attempt == 1
                ? JwksResponse(jwksRsa, jwksKid)
                : throw new HttpRequestException("jwks endpoint unreachable");
        });
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/.well-known/jwks.json";

        var ring = new SigningKeyRing(options, new HttpClient(handler));

        // First call succeeds and populates the cache.
        var firstKeys = ring.Resolve(jwksKid).ToList();
        Assert.Single(firstKeys);

        // Force the next call to actually re-hit the (now-failing) handler instead of
        // being absorbed by the unknown-kid/interval floors.
        RewindLastFetch(ring);

        var exception = Record.Exception(() => ring.Resolve(jwksKid).ToList());

        Assert.Null(exception);
        Assert.Equal(2, handler.CallCount);

        var keysAfterFailure = ring.Resolve(jwksKid).ToList();
        Assert.Single(keysAfterFailure);
        Assert.Equal(jwksKid, keysAfterFailure[0].KeyId);
    }
}
