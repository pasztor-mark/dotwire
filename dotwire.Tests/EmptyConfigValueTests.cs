using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

public class EmptyConfigValueTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Dotwire:AuthBatching:Enabled")]
    [InlineData("Dotwire:AuthBatching:MaxInFlight")]
    [InlineData("Dotwire:AuthBatching:MaxBatchSize")]
    [InlineData("Dotwire:AuthCacheSeconds")]
    [InlineData("Dotwire:DisableDebugLogging")]
    [InlineData("Dotwire:DisableAllLogging")]
    [InlineData("Dotwire:HubSend:Enabled")]
    [InlineData("Dotwire:MembershipSweepSeconds")]
    [InlineData("Dotwire:Role")]
    public async Task EmptyValue_StartsWithDefaults(string key)
    {
        var app = OfflineApp.Create(factory, configure: b => b.UseSetting(key, ""));
        var response = await app.CreateClient().GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
