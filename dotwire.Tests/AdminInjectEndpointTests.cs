using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// In-process auth gate for admin message injection (no Postgres/NATS). Everything past the
/// gate - publish, live seq, audit event - is covered by ComposeIntegrationTests
/// (spec §3.3).
/// </summary>
public class AdminInjectEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminInjectEndpointTests(WebApplicationFactory<Program> factory)
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

    private static readonly string Route = $"/admin/rooms/{Guid.NewGuid()}/messages";

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task NoToken_Is401()
    {
        var response = await Client().PostAsJsonAsync(Route, new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MemberToken_Is403()
    {
        // RequireAdminFilter rejects on the claim before it ever consults user_roles, so
        // this holds without a database: a member can never reach the injection path.
        var response = await Client(TestTokens.Mint("user-1", role: "member"))
            .PostAsJsonAsync(Route, new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuditorToken_Is403()
    {
        var response = await Client(TestTokens.Mint("user-1", role: "auditor"))
            .PostAsJsonAsync(Route, new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
