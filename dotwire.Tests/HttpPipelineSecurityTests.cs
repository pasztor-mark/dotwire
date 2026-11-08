using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dotwire;
using Dotwire.Api;
using Dotwire.Nats;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class HttpPipelineSecurityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid Room = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private (WebApplicationFactory<Program> App, FakeAccessStore Store) App(
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        var store = new FakeAccessStore();
        return (OfflineApp.Create(factory, store, configure, services), store);
    }

    [Fact]
    public async Task RoleCrossCheck_TokenAdminButTableMember_Is403()
    {
        var (app, store) = App();
        store.Roles["u1"] = "member";
        store.Members[(Room, "u1")] = true;

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("u1", role: "admin"))
            .GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, store.MembershipChecks);
    }

    [Fact]
    public async Task RoleCrossCheck_NoRoleRow_Is403()
    {
        var (app, store) = App();
        store.Members[(Room, "u1")] = true;

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("u1"))
            .GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoomMembership_NonUuidRoom_Is404_And_NonMember_Is403()
    {
        var (app, store) = App();
        store.Roles["u1"] = "member";
        var client = WithToken(app.CreateClient(), TestTokens.Mint("u1"));

        var nonUuid = await client.GetAsync("/rooms/not-a-uuid/participants", Ct);
        var nonMember = await client.GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.NotFound, nonUuid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
    }

    [Theory]
    [InlineData("admin", "/admin/users/someone/role")]
    [InlineData("auditor", "/audit")]
    public async Task PrivilegedRoute_RoleRevokedInTable_Is403(string role, string path)
    {
        var (app, store) = App();
        store.Roles["p1"] = "member";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("p1", role: role)).GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AdminResponses_AreNotCacheable()
    {
        var (app, store) = App();
        store.Roles["a1"] = "admin";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("a1", role: "admin"))
            .GetAsync($"/admin/users/{new string('x', 300)}/role", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task AddMembers_OverCap_Is400BeforeTouchingTheDatabase()
    {
        var (app, store) = App();
        store.Roles["a1"] = "admin";
        var ids = Enumerable.Range(0, Admin.MaxMembersPerRequest + 1).Select(i => $"u{i}").ToArray();

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("a1", role: "admin"))
            .PostAsJsonAsync($"/admin/rooms/{Room}/members", new { userIds = ids }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Admin_CannotChangeOwnRole(string method)
    {
        var (app, store) = App();
        store.Roles["a1"] = "admin";
        var request = new HttpRequestMessage(new HttpMethod(method), "/admin/users/a1/role");
        if (method == "PUT")
            request.Content = JsonContent.Create(new { role = "member" });

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("a1", role: "admin")).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("self_role_change", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Redact_SeqBeyondInt64_Is400()
    {
        var (app, store) = App();
        store.Roles["a1"] = "admin";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("a1", role: "admin"))
            .DeleteAsync($"/admin/rooms/{Room}/messages/{(ulong)long.MaxValue + 1}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Send_MultibyteContentOverLimit_Is400()
    {
        var (app, store) = App(b => b.UseSetting("Dotwire:MaxContentBytes", "10"));
        store.Roles["u1"] = "member";
        store.Members[(Room, "u1")] = true;

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("u1"))
            .PostAsJsonAsync($"/rooms/{Room}/messages", new { content = new string('é', 6) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_content", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Send_InvalidIdempotencyKey_Is400()
    {
        var (app, store) = App();
        store.Roles["u1"] = "member";
        store.Members[(Room, "u1")] = true;
        var client = WithToken(app.CreateClient(), TestTokens.Mint("u1"));
        client.DefaultRequestHeaders.Add("Idempotency-Key", new string('k', Messages.MaxIdempotencyKeyLength + 1));

        var response = await client.PostAsJsonAsync($"/rooms/{Room}/messages", new { content = "hi" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task OversizedBody_Is413_BeforeAuthentication()
    {
        var (app, _) = App(b => b.UseSetting("Dotwire:MaxRequestBodyBytes", "1024"));
        var content = new StringContent(new string('a', 2048), System.Text.Encoding.UTF8, "application/json");

        var response = await app.CreateClient().PostAsync($"/rooms/{Room}/messages", content, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task QueryToken_IsIgnoredOutsideSseAndHubRoutes()
    {
        var (app, store) = App();
        store.Roles["a1"] = "admin";
        var token = TestTokens.Mint("a1", role: "admin");

        var response = await app.CreateClient()
            .DeleteAsync($"/admin/rooms/{Room}/members/events?access_token={token}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task QueryToken_IsAcceptedOnSseRoute()
    {
        var (app, store) = App(b => b.UseSetting("Dotwire:Sse:Enabled", "false"));
        store.Roles["u1"] = "member";
        store.Members[(Room, "u1")] = true;

        var response = await app.CreateClient()
            .GetAsync($"/rooms/{Room}/events?access_token={TestTokens.Mint("u1")}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutIat_Is401()
    {
        var (app, store) = App();
        store.Roles["u1"] = "member";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("u1", includeIat: false))
            .GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenLifetimeOverCap_Is401()
    {
        var (app, store) = App();
        store.Roles["u1"] = "member";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("u1", ttl: TimeSpan.FromHours(2)))
            .GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenLifetimeCap_CanBeRaised()
    {
        var (app, store) = App(b => b.UseSetting("Auth:MaxTokenLifetimeMinutes", "180"));
        store.Roles["u1"] = "member";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("u1", ttl: TimeSpan.FromHours(2)))
            .GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithControlCharacterSub_Is401()
    {
        var (app, store) = App();
        store.Roles["a\nb"] = "member";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("a\nb"))
            .GetAsync($"/rooms/{Room}/participants", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cors_AllowlistRejectsOtherOrigins_AndNeverAllowsCredentials()
    {
        var (app, _) = App(b => b.UseSetting("Cors:AllowedOrigins:0", "https://app.example"));
        var client = app.CreateClient();

        HttpRequestMessage Preflight(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, $"/rooms/{Room}/messages");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            return request;
        }

        var allowed = await client.SendAsync(Preflight("https://app.example"), Ct);
        var denied = await client.SendAsync(Preflight("https://evil.example"), Ct);

        Assert.Equal("https://app.example", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_DefaultIsWildcardWithoutCredentials()
    {
        var (app, _) = App();
        var request = new HttpRequestMessage(HttpMethod.Options, $"/rooms/{Room}/messages");
        request.Headers.Add("Origin", "https://any.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await app.CreateClient().SendAsync(request, Ct);

        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task UnauthenticatedTraffic_IsRateLimited()
    {
        var (app, _) = App(b =>
        {
            b.UseSetting("Dotwire:RateLimits:Unauthenticated:PermitLimit", "2");
            b.UseSetting("Dotwire:RateLimits:Unauthenticated:TokensPerPeriod", "1");
            b.UseSetting("Dotwire:RateLimits:Unauthenticated:PeriodSeconds", "3600");
        });
        var client = WithToken(app.CreateClient(), "not.a.jwt");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await client.GetAsync($"/rooms/{Room}/participants", Ct)).StatusCode);

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public void HubClientTimeout_IsTwiceTheKeepAlive()
    {
        var (app, _) = App();
        var hubOptions = app.Services.GetRequiredService<IOptions<HubOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(60), hubOptions.ClientTimeoutInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), hubOptions.KeepAliveInterval);
    }

    [Fact]
    public void WebhookHttpClient_CapsResponseBodies()
    {
        var (app, _) = App();
        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PresendWebhookClient.HttpClientName);

        Assert.Equal(PresendWebhookClient.MaxResponseBytes, client.MaxResponseContentBufferSize);
    }

    [Fact]
    public void InsecureWebhookUrl_FailsStartup()
    {
        var (app, _) = App(b => b.UseSetting("Dotwire:Webhooks:Presend:Url", "http://hooks.example/presend"));

        Assert.Throws<InvalidOperationException>(() => app.Services);
    }

    [Fact]
    public async Task HealthProbe_ReportsHealthAndFailure()
    {
        var (app, _) = App();

        var healthy = await HealthProbe.RunAsync(app.CreateClient(), new Uri(app.Server.BaseAddress, "/healthz"));
        var missing = await HealthProbe.RunAsync(app.CreateClient(), new Uri(app.Server.BaseAddress, "/nope"));

        Assert.Equal(0, healthy);
        Assert.Equal(1, missing);
    }

    [Fact]
    public async Task Dsar_AuditPublishFails_Is503WithoutExportData()
    {
        var publisher = new ThrowingAuditPublisher();
        var (app, store) = App(services: s => s.AddSingleton<AuditPublisher>(publisher));
        store.Roles["a1"] = "admin";

        var response = await WithToken(app.CreateClient(), TestTokens.Mint("a1", role: "admin"))
            .GetAsync("/admin/users/u2/export", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("audit_unavailable", body);
        Assert.DoesNotContain("messages", body);
        Assert.Equal(1, publisher.Attempts);
    }

    [Fact]
    public async Task FullAuditVerify_IsSingleFlight()
    {
        var (app, store) = App();
        store.Roles["au1"] = "auditor";
        var client = WithToken(app.CreateClient(), TestTokens.Mint("au1", role: "auditor"));

        Assert.True(Audit.FullVerifyGate.Wait(0, TestContext.Current.CancellationToken));
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync("/audit/verify?full=true", Ct);
        }
        finally
        {
            Audit.FullVerifyGate.Release();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
