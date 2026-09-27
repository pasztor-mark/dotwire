using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// In-process auth gate for the redaction endpoint (no Postgres/NATS). Everything past the
/// gate - erasure, audit event, retraction - is covered by ComposeIntegrationTests.
/// </summary>
public class AdminRedactEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminRedactEndpointTests(WebApplicationFactory<Program> factory)
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

    private static readonly string Route = $"/admin/rooms/{Guid.NewGuid()}/messages/42";

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
        var response = await Client().DeleteAsync(Route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MemberToken_Is403()
    {
        // RequireAdminFilter rejects on the claim before it ever consults user_roles, so
        // this holds without a database: a member can never reach the erasure path.
        var response = await Client(TestTokens.Mint("user-1", role: "member"))
            .DeleteAsync(Route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuditorToken_Is403()
    {
        var response = await Client(TestTokens.Mint("user-1", role: "auditor"))
            .DeleteAsync(Route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
