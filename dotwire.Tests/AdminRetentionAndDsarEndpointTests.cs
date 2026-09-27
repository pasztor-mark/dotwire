using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// In-process auth gates for retention (§3.11) and DSAR export (§3.12) - no Postgres/NATS.
/// Everything past the gate is covered by ComposeIntegrationTests.
/// </summary>
public class AdminRetentionAndDsarEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminRetentionAndDsarEndpointTests(WebApplicationFactory<Program> factory)
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

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Retention_Get_NoToken_Is401()
    {
        var response = await Client().GetAsync("/admin/retention", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Retention_Put_MemberToken_Is403()
    {
        var response = await Client(TestTokens.Mint("user-1", role: "member"))
            .PutAsJsonAsync("/admin/retention", new { messagesDays = 10 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_NoToken_Is401()
    {
        var response = await Client().GetAsync("/admin/users/user-1/export", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_AuditorToken_Is403()
    {
        // Admin-only per spec §3.12; auditors get /audit* read access, not DSAR.
        var response = await Client(TestTokens.Mint("user-1", role: "auditor"))
            .GetAsync("/admin/users/user-1/export", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
