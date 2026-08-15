using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace dotwire.Tests;

public class SendEndpointAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SendEndpointAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Postgres:Migrate", "false");
            b.UseSetting("Nats:Enabled", "false");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
        });
    }

    private static readonly string Route = $"/rooms/{Guid.NewGuid()}/messages";

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client) =>
        client.PostAsJsonAsync(Route, new { content = "hi" });

    [Fact]
    public async Task NoToken_Is401()
    {
        var response = await Send(Client());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GarbageToken_Is401()
    {
        var response = await Send(Client("not.a.jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Is401()
    {
        var token = TestTokens.Mint("user-1", ttl: TimeSpan.FromMinutes(-30));
        var response = await Send(Client(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongAudience_Is401()
    {
        var token = TestTokens.Mint("user-1", audience: "someone-else");
        var response = await Send(Client(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongIssuer_Is401()
    {
        var token = TestTokens.Mint("user-1", issuer: "evil-host");
        var response = await Send(Client(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownKid_Is401()
    {
        var token = TestTokens.Mint("user-1", rsa: RSA.Create(2048), kid: "unknown-kid");
        var response = await Send(Client(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingRole_Is401()
    {
        var token = TestTokens.Mint("user-1", role: null);
        var response = await Send(Client(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingKid_Is401()
    {
        // AUTH.md, "Token contract": kid is required in every token's header. A validly
        // signed token that simply omits kid must still be rejected (SigningKeyRing fails
        // closed on a null/empty kid rather than trying every configured key).
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            Claims = new Dictionary<string, object> { ["sub"] = "user-1", ["dw:role"] = "member" },
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(TestKeys.Rsa), // no KeyId set, no kid header
                SecurityAlgorithms.RsaSha256),
        });

        var response = await Send(Client(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hs256Token_Is401()
    {
        // RS256 only . a symmetric token must never validate (AUTH.md, "Threat notes").
        var hs256 = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            Claims = new Dictionary<string, object> { ["sub"] = "user-1", ["dw:role"] = "member" },
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = TestKeys.Kid },
                SecurityAlgorithms.HmacSha256),
        });

        var response = await Send(Client(hs256));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
