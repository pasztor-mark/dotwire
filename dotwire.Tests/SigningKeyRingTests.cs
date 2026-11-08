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
    public async Task ResolvesJwksKeyByKidAfterRefresh()
    {
        var jwksRsa = RSA.Create(2048);
        const string jwksKid = "jwks-key-1";
        var handler = new FakeJwksHandler(() => JwksResponse(jwksRsa, jwksKid));
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/.well-known/jwks.json";

        var ring = new SigningKeyRing(options, new HttpClient(handler));
        await ring.RefreshAsync();
        var keys = ring.Resolve(jwksKid).ToList();

        Assert.Single(keys);
        Assert.Equal(jwksKid, keys[0].KeyId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task UnknownKidWithinFloorDoesNotRefetch()
    {
        var jwksRsa = RSA.Create(2048);
        const string jwksKid = "jwks-key-1";
        var handler = new FakeJwksHandler(() => JwksResponse(jwksRsa, jwksKid));
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/.well-known/jwks.json";

        var ring = new SigningKeyRing(options, new HttpClient(handler));
        await ring.RefreshAsync();

        var keys = ring.Resolve("some-other-kid").ToList();

        Assert.Empty(keys);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchFailureKeepsPreviousKeysAndDoesNotThrow()
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
        await ring.RefreshAsync();
        Assert.Single(ring.Resolve(jwksKid));

        var exception = await Record.ExceptionAsync(ring.RefreshAsync);

        Assert.Null(exception);
        Assert.Equal(2, handler.CallCount);
        Assert.Single(ring.Resolve(jwksKid));
    }

    [Fact]
    public async Task UnknownKidNeverBlocksTheCaller()
    {
        var jwksRsa = RSA.Create(2048);
        var release = new TaskCompletionSource();
        var handler = new SlowJwksHandler(release.Task, () => JwksResponse(jwksRsa, "late-kid"));
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/.well-known/jwks.json";
        var ring = new SigningKeyRing(options, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) });

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var keys = ring.Resolve("late-kid").ToList();
        elapsed.Stop();

        Assert.Empty(keys);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Resolve blocked for {elapsed.Elapsed}");

        release.SetResult();
        await ring.RefreshAsync();
        Assert.Single(ring.Resolve("late-kid"));
    }

    [Theory]
    [InlineData("""{"keys":[]}""")]
    [InlineData("not json")]
    public async Task EmptyOrInvalidJwksKeepsPreviousKeys(string body)
    {
        var jwksRsa = RSA.Create(2048);
        var calls = 0;
        var handler = new FakeJwksHandler(() => ++calls == 1
            ? JwksResponse(jwksRsa, "k")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/jwks";
        var ring = new SigningKeyRing(options, new HttpClient(handler));

        await ring.RefreshAsync();
        await ring.RefreshAsync();

        Assert.Equal(2, calls);
        Assert.Single(ring.Resolve("k"));
    }

    [Fact]
    public async Task JwksWithOnlyWeakKeysKeepsPreviousKeys()
    {
        var strong = RSA.Create(2048);
        var weak = RSA.Create(1024);
        var calls = 0;
        var handler = new FakeJwksHandler(() => ++calls == 1 ? JwksResponse(strong, "k") : JwksResponse(weak, "k"));
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/jwks";
        var ring = new SigningKeyRing(options, new HttpClient(handler));

        await ring.RefreshAsync();
        await ring.RefreshAsync();

        var key = Assert.Single(ring.Resolve("k"));
        Assert.Equal(2048, key.KeySize);
    }

    [Fact]
    public async Task OversizedJwksIsRejected()
    {
        var calls = 0;
        var handler = new FakeJwksHandler(() =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string(' ', SigningKeyRing.MaxJwksBytes + 1) + """{"keys":[]}"""),
            };
        });
        var options = ValidOptions();
        options.JwksUrl = "https://issuer.example/jwks";
        var ring = new SigningKeyRing(options, new HttpClient(handler));

        await ring.RefreshAsync();

        Assert.Equal(1, calls);
        Assert.Empty(ring.Resolve("k"));
    }

    [Theory]
    [InlineData("http://issuer.example/jwks", false, true)]
    [InlineData("http://issuer.example/jwks", true, false)]
    [InlineData("http://localhost:5000/jwks", false, false)]
    [InlineData("http://127.0.0.1:5000/jwks", false, false)]
    [InlineData("https://issuer.example/jwks", false, false)]
    [InlineData("ftp://issuer.example/jwks", false, true)]
    public void JwksUrlMustBeHttpsUnlessLoopbackOrOverridden(string url, bool allowInsecure, bool throws)
    {
        var options = ValidOptions();
        options.JwksUrl = url;
        options.AllowInsecureJwksUrl = allowInsecure;

        var exception = Record.Exception(() => new SigningKeyRing(options));

        Assert.Equal(throws, exception is InvalidOperationException);
    }

    [Fact]
    public void WeakInlineKeyIsRejected()
    {
        var options = ValidOptions();
        options.Keys["weak"] = RSA.Create(1024).ExportSubjectPublicKeyInfoPem();

        Assert.Throws<InvalidOperationException>(() => new SigningKeyRing(options));
    }

    [Fact]
    public void PrivateKeyPemIsRejected()
    {
        var options = ValidOptions();
        options.Keys["oops"] = RSA.Create(2048).ExportPkcs8PrivateKeyPem();

        Assert.Throws<InvalidOperationException>(() => new SigningKeyRing(options));
    }

    [Fact]
    public async Task InlineKeyShadowsJwksKeyWithTheSameKid()
    {
        var inlineRsa = RSA.Create(2048);
        var jwksRsa = RSA.Create(2048);
        var handler = new FakeJwksHandler(() => JwksResponse(jwksRsa, "shared"));
        var options = ValidOptions();
        options.Keys["shared"] = inlineRsa.ExportSubjectPublicKeyInfoPem();
        options.JwksUrl = "https://issuer.example/jwks";
        var ring = new SigningKeyRing(options, new HttpClient(handler));
        await ring.RefreshAsync();

        var key = Assert.IsType<RsaSecurityKey>(Assert.Single(ring.Resolve("shared")));

        Assert.Equal(inlineRsa.ExportParameters(false).Modulus, key.Rsa.ExportParameters(false).Modulus);
    }

    private sealed class SlowJwksHandler(Task gate, Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return respond();
        }
    }
}
