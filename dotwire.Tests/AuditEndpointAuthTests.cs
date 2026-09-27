using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// In-process auth gate for `/audit*` (no Postgres/NATS). Admin and member both get 403 -
/// admin does not get audit access (AUTH.md, COMPLIANCE.md, spec §3.7). Everything past the
/// gate (pagination, verify, checkpoints, the audit.read side-effect) is covered by
/// AuditEndpointsComposeTests.
/// </summary>
public class AuditEndpointAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuditEndpointAuthTests(WebApplicationFactory<Program> factory)
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

    public static IEnumerable<object[]> Routes =>
    [
        ["/audit"],
        ["/audit/verify"],
        ["/audit/checkpoints"],
    ];

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task NoToken_Is401(string route)
    {
        var response = await Client().GetAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task MemberToken_Is403(string route)
    {
        var response = await Client(TestTokens.Mint("user-1", role: "member"))
            .GetAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AdminToken_Is403(string route)
    {
        // RequireAuditorFilter rejects on the claim before it ever consults user_roles, so
        // this holds without a database: admin can never reach the audit-read path.
        var response = await Client(TestTokens.Mint("user-1", role: "admin"))
            .GetAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
