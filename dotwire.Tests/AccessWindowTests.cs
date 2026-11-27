using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Nats;
using Dotwire.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Xunit;

namespace dotwire.Tests;

public class AccessWindowTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid RoomId = Guid.NewGuid();

    public static AccessLogPublisher Disabled() => new(
        null!,
        Options.Create(new NatsOptions()),
        Options.Create(new AccessLogOptions { Enabled = false }),
        TestMetrics.Create().Metrics,
        NullLogger<AccessLogPublisher>.Instance);

    private static RoomInterestManager Manager(AccessLogPublisher accessLog, INatsConnection? nats = null, bool natsEnabled = false)
    {
        nats ??= DispatchProxy.Create<INatsConnection, RealtimeFanoutUnitTests.CountingNats>();
        var encryption = new EncryptionOptions { ActiveKeyId = "k1" };
        encryption.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new RoomInterestManager(
            nats,
            new RoomHubLifetimeManager(NullLogger<DefaultHubLifetimeManager<RoomHub>>.Instance),
            new MessageCipher(Options.Create(encryption)),
            new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = false }), NullLogger<PresenceCoalescer>.Instance),
            Options.Create(new NatsOptions { Enabled = natsEnabled }),
            NullLogger<RoomInterestManager>.Instance,
            TestMetrics.Create().Metrics,
            accessLog);
    }

    private static AccessEvent Open(Guid roomId, string via = AccessEvents.Hub) => new(
        AccessEvents.Opened, DateTimeOffset.UtcNow, roomId, "u1", null, "member", via, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task Subscribe_ThenUnsubscribe_WritesOneLeft()
    {
        var log = new RecordingAccessLogPublisher();
        var manager = Manager(log);
        var open = Open(RoomId);
        await manager.SubscribeAsync(RoomId, "c1", "u1", "member", open);

        Assert.True(manager.Unsubscribe(RoomId, "c1"));
        Assert.False(manager.Unsubscribe(RoomId, "c1"));
        manager.DisconnectAll("c1");

        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Closed, close.Event);
        Assert.Equal(AccessEvents.Left, close.Reason);
        Assert.Equal(open.WindowId, close.WindowId);
    }

    [Fact]
    public async Task DisconnectAll_ThreeRooms_WritesThreeDisconnected()
    {
        var log = new RecordingAccessLogPublisher();
        var manager = Manager(log);
        var opens = new List<AccessEvent>();
        for (var i = 0; i < 3; i++)
        {
            var open = Open(Guid.NewGuid());
            opens.Add(open);
            await manager.SubscribeAsync(open.RoomId, "c1", "u1", "member", open);
        }

        manager.DisconnectAll("c1");
        manager.DisconnectAll("c1");

        Assert.Equal(3, log.Closes.Count);
        Assert.All(log.Closes, c => Assert.Equal(AccessEvents.Disconnected, c.Reason));
        Assert.Equal(opens.Select(o => o.WindowId).Order(), log.Closes.Select(c => c.WindowId).Order());
        Assert.Equal(3, log.Closes.Select(c => c.WindowId).Distinct().Count());
    }

    [Fact]
    public async Task Revoke_WritesOneRevoked()
    {
        var log = new RecordingAccessLogPublisher();
        var manager = Manager(log);
        var open = Open(RoomId);
        await manager.SubscribeAsync(RoomId, "c1", "u1", "member", open);

        manager.Revoke(RoomId, "u1");
        manager.Revoke(RoomId, "u1");
        manager.DisconnectAll("c1");

        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Revoked, close.Reason);
        Assert.Equal(open.WindowId, close.WindowId);
    }

    [Fact]
    public async Task Revoke_ThenSseFinally_WritesOneRevokedClose()
    {
        var log = new RecordingAccessLogPublisher();
        var manager = Manager(log);
        var open = Open(RoomId, AccessEvents.Sse);
        await manager.RegisterSseConnectionAsync(RoomId, "sse-1", "u1", 8, "member", open);

        manager.Revoke(RoomId, "u1");
        manager.UnregisterSseConnection(RoomId, "sse-1", AccessEvents.Disconnected);

        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Revoked, close.Reason);
        Assert.Equal(open.WindowId, close.WindowId);
    }

    [Fact]
    public async Task FailSubscription_ThenDisconnectAll_WritesOneError()
    {
        var log = new RecordingAccessLogPublisher();
        var nats = DispatchProxy.Create<INatsConnection, RealtimeFanoutUnitTests.SubjectSubscribingNats>();
        var counting = (RealtimeFanoutUnitTests.SubjectSubscribingNats)(object)nats;
        var manager = Manager(log, nats, natsEnabled: true);
        var open = Open(RoomId, AccessEvents.Sse);
        await manager.RegisterSseConnectionAsync(RoomId, "sse-1", "u1", 8, "member", open);

        lock (counting.Subjects)
            counting.Subjects.Single(s => !s.Key.Contains("ephemeral")).Value.Writer.Complete();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Closes.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);

        manager.DisconnectAll("sse-1");
        manager.UnregisterSseConnection(RoomId, "sse-1");

        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Error, close.Reason);
        Assert.Equal(open.WindowId, close.WindowId);
    }

    [Fact]
    public void OpenEvent_WithoutExp_ReturnsNull()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "u1"), new System.Security.Claims.Claim("dw:role", "member")], "test"));

        Assert.Null(AccessEvents.OpenEvent(user, RoomId, AccessEvents.Hub));
    }

    [Fact]
    public async Task Subscribe_TokenWithoutExp_ThrowsAccessLogUnavailable_NotSubscribed()
    {
        var log = new RecordingAccessLogPublisher();
        var manager = Manager(log);
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        store.Members[(RoomId, "u1")] = true;
        var nats = DispatchProxy.Create<INatsConnection, RealtimeFanoutUnitTests.CountingNats>();
        var hub = new RoomHub(
            store, manager,
            new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = false }), NullLogger<PresenceCoalescer>.Instance),
            new TypingThrottle(),
            new UserConnectionLimiter(Options.Create(new DotwireOptions())),
            nats, Options.Create(new NatsOptions()), null!, null!, Options.Create(new DotwireOptions()),
            TestMetrics.Create().Metrics, log)
        {
            Context = new NoExpContext(),
        };

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.Subscribe(RoomId));

        Assert.Equal("AccessLogUnavailable", ex.Message);
        Assert.Empty(manager.LiveSubscriptions());
        Assert.Empty(log.Recorded);
    }

    private sealed class NoExpContext : HubCallerContext
    {
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User { get; } = new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "u1"), new System.Security.Claims.Claim("dw:role", "member")], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    [Fact]
    public async Task DisabledPublisher_NoEvents()
    {
        var manager = Manager(Disabled());
        await manager.SubscribeAsync(RoomId, "c1", "u1", "member");

        Assert.True(manager.Unsubscribe(RoomId, "c1"));
    }

    private (WebApplicationFactory<Program> App, FakeAccessStore Store, RecordingAccessLogPublisher Log, Guid Room) HubApp(bool member = true)
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        var room = Guid.NewGuid();
        if (member)
            store.Members[(room, "u1")] = true;
        var log = new RecordingAccessLogPublisher();
        var app = OfflineApp.Create(factory, store, services: s => s.AddSingleton<AccessLogPublisher>(log));
        return (app, store, log, room);
    }

    private static HubConnection Connect(WebApplicationFactory<Program> app, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hub/rooms"), options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var ws = app.Server.CreateWebSocketClient();
                    ws.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                    return await ws.ConnectAsync(context.Uri, ct);
                };
            })
            .Build();

    [Fact]
    public async Task Subscribe_RecordsOpenBeforeJoin()
    {
        var (app, _, log, room) = HubApp();
        var manager = app.Services.GetRequiredService<RoomInterestManager>();
        var token = TestTokens.Mint("u1", ttl: TimeSpan.FromMinutes(30));
        var exp = long.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("exp").Value);

        await using var connection = Connect(app, token);
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("Subscribe", room, Ct);

        var open = Assert.Single(log.Recorded);
        Assert.Equal(AccessEvents.Opened, open.Event);
        Assert.Equal(AccessEvents.Hub, open.Via);
        Assert.Equal("member", open.Role);
        Assert.Equal("u1", open.UserId);
        Assert.Equal(room, open.RoomId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp), open.ExpiresAt);
        Assert.NotNull(open.WindowId);
        Assert.Single(manager.LiveSubscriptions());

        await connection.StopAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Closes.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Disconnected, close.Reason);
        Assert.Equal(open.WindowId, close.WindowId);
    }

    [Fact]
    public async Task Subscribe_FailingPublisher_ThrowsAccessLogUnavailable_NotSubscribed()
    {
        var (app, _, log, room) = HubApp();
        log.FailRecords = true;

        await using var connection = Connect(app, TestTokens.Mint("u1"));
        await connection.StartAsync(Ct);
        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", room, Ct));

        Assert.Contains("AccessLogUnavailable", ex.Message);
        Assert.Empty(app.Services.GetRequiredService<RoomInterestManager>().LiveSubscriptions());
        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Error, close.Reason);
        Assert.Equal(log.Recorded[0].WindowId, close.WindowId);
    }

    [Fact]
    public async Task Subscribe_Twice_RecordsOneOpen()
    {
        var (app, _, log, room) = HubApp();

        await using var connection = Connect(app, TestTokens.Mint("u1"));
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("Subscribe", room, Ct);
        await connection.InvokeAsync("Subscribe", room, Ct);

        Assert.Single(log.Recorded);
        Assert.Empty(log.Closes);
    }

    [Fact]
    public async Task Sse_Open_RecordsActor()
    {
        var (app, _, log, room) = HubApp();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Mint("u1", actor: "svc"));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/rooms/{room}/events"), HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var open = Assert.Single(log.Recorded);
        Assert.Equal(AccessEvents.Opened, open.Event);
        Assert.Equal(AccessEvents.Sse, open.Via);
        Assert.Equal("svc", open.ActorId);
    }

    [Fact]
    public async Task Sse_RecheckFails_403_OneRevokedClose()
    {
        var (app, store, log, room) = HubApp();
        store.MembershipOverride = (_, _, call) => call == 1;
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Mint("u1"));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/rooms/{room}/events"), HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Revoked, close.Reason);
        Assert.Equal(log.Recorded[0].WindowId, close.WindowId);
        Assert.Empty(app.Services.GetRequiredService<RoomInterestManager>().LiveSubscriptions());
    }

    [Fact]
    public async Task Subscribe_RecheckFails_WritesRevokedClose()
    {
        var (app, store, log, room) = HubApp();
        store.MembershipOverride = (_, _, call) => call == 1;

        await using var connection = Connect(app, TestTokens.Mint("u1"));
        await connection.StartAsync(Ct);
        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", room, Ct));

        Assert.Contains("Forbidden", ex.Message);
        var close = Assert.Single(log.Closes);
        Assert.Equal(AccessEvents.Revoked, close.Reason);
        Assert.Equal(log.Recorded[0].WindowId, close.WindowId);
        Assert.Empty(app.Services.GetRequiredService<RoomInterestManager>().LiveSubscriptions());
    }
}
