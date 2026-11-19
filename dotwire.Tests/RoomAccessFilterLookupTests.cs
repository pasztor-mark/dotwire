using System.Net;
using System.Net.Http.Headers;
using Dotwire.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace dotwire.Tests;

public class RoomAccessFilterLookupTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid Room = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (WebApplicationFactory<Program> App, FakeAccessStore Store) App(bool batching, int cacheSeconds, string? role = null)
    {
        var store = new FakeAccessStore();
        var app = OfflineApp.Create(factory, store, b =>
        {
            b.UseSetting("Dotwire:AuthBatching:Enabled", batching ? "true" : "false");
            b.UseSetting("Dotwire:AuthCacheSeconds", cacheSeconds.ToString());
            if (role is not null) b.UseSetting("Dotwire:Role", role);
        });
        return (app, store);
    }

    private static HttpClient Client(WebApplicationFactory<Program> app, string user = "u1", string role = "member")
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(user, role: role));
        return client;
    }

    private static Task<HttpResponseMessage> Get(HttpClient c) => c.GetAsync($"/rooms/{Room}/participants", Ct);

    private static void Seed(FakeAccessStore store, bool member = true)
    {
        store.Roles["u1"] = "member";
        if (member) store.Members[(Room, "u1")] = true;
    }

    [Fact]
    public async Task Defaults_NoBatcherNoCache_OneCombinedCallPerRequest()
    {
        var (app, store) = App(false, 0);
        Seed(store);
        Assert.Null(app.Services.GetService<AccessLookupBatcher>());
        Assert.Null(app.Services.GetService<AccessCache>());
        var client = Client(app);
        await Get(client);
        await Get(client);
        Assert.Equal(2, store.CombinedChecks);
    }

    [Fact]
    public async Task BatchingOn_LoneRequest_StillOneCombinedCall()
    {
        var (app, store) = App(true, 0);
        Seed(store);
        Assert.NotNull(app.Services.GetService<AccessLookupBatcher>());
        var response = await Get(Client(app));
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, store.CombinedChecks);
        Assert.Equal(0, store.BatchChecks);
    }

    [Fact]
    public async Task BatchingOn_NonMember403_RoleMismatch403()
    {
        var (app, store) = App(true, 0);
        Seed(store, member: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(Client(app))).StatusCode);
        store.Members[(Room, "u1")] = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(Client(app, role: "admin"))).StatusCode);
    }

    [Fact]
    public async Task CacheConfigured_NatsDisabled_EveryRequestHitsStore()
    {
        var (app, store) = App(false, 30);
        Seed(store);
        Assert.False(app.Services.GetRequiredService<AccessCache>().IsUsable);
        var client = Client(app);
        for (var i = 0; i < 3; i++) await Get(client);
        Assert.Equal(3, store.CombinedChecks);
    }

    [Fact]
    public async Task CacheResumed_SecondRequestServedFromCache()
    {
        var (app, store) = App(false, 30);
        Seed(store);
        app.Services.GetRequiredService<AccessCache>().Resume();
        var client = Client(app);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Get(client)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Get(client)).StatusCode);
        Assert.Equal(1, store.CombinedChecks);
    }

    [Fact]
    public async Task CacheResumed_EvictForcesReadAndDenies()
    {
        var (app, store) = App(false, 30);
        Seed(store);
        var cache = app.Services.GetRequiredService<AccessCache>();
        cache.Resume();
        var client = Client(app);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Get(client)).StatusCode);
        store.Members.TryRemove((Room, "u1"), out _);
        cache.Evict(Room, "u1");
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(client)).StatusCode);
        Assert.Equal(2, store.CombinedChecks);
    }

    [Fact]
    public async Task CacheResumed_DenialNotCached()
    {
        var (app, store) = App(false, 30);
        Seed(store, member: false);
        app.Services.GetRequiredService<AccessCache>().Resume();
        var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(client)).StatusCode);
        store.Members[(Room, "u1")] = true;
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Get(client)).StatusCode);
        Assert.Equal(2, store.CombinedChecks);
    }

    [Fact]
    public async Task CacheResumed_CachedRoleMismatch_Is403WithoutRead()
    {
        var (app, store) = App(false, 30);
        Seed(store);
        app.Services.GetRequiredService<AccessCache>().Resume();
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Get(Client(app))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(Client(app, role: "admin"))).StatusCode);
        Assert.Equal(1, store.CombinedChecks);
    }

    [Fact]
    public void GatewayNode_NoCacheRegistered()
    {
        var (app, _) = App(false, 30, role: "Gateway");
        Assert.Null(app.Services.GetService<AccessCache>());
    }
}
