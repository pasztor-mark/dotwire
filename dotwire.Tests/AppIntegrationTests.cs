using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

public class AppIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AppIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // In-process tests run without Postgres or NATS; migrations and provisioning are
        // covered by compose-based checks.
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Postgres:Migrate", "false");
            b.UseSetting("ConnectionStrings:PostgresRead", "Host=localhost;Database=dotwire");
            b.UseSetting("ConnectionStrings:PostgresWrite", "Host=localhost;Database=dotwire");
            b.UseSetting("Nats:Enabled", "false");
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "t1");
            b.UseSetting("Dotwire:Encryption:Keys:t1", Convert.ToBase64String(new byte[32]));
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
        });
    }

    [Fact]
    public async Task AppStartsAndResponds()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HealthzReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
