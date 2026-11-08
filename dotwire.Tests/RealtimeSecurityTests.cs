using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Channels;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Xunit;

namespace dotwire.Tests;

public class RealtimeSecurityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public async Task Subscribe_RoleMismatch_IsForbidden()
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "auditor";
        var room = Guid.NewGuid();
        store.Members[(room, "u1")] = true;
        var app = OfflineApp.Create(factory, store);

        await using var connection = Connect(app, TestTokens.Mint("u1"));
        await connection.StartAsync(Ct);
        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", room, Ct));

        Assert.Contains("Forbidden", ex.Message);
        Assert.Empty(app.Services.GetRequiredService<RoomInterestManager>().LiveSubscriptions());
    }

    [Fact]
    public async Task Subscribe_RevokedWhileRegistering_IsForbiddenAndUnsubscribed()
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        var room = Guid.NewGuid();
        store.MembershipOverride = (_, _, call) => call == 1;
        var app = OfflineApp.Create(factory, store);

        await using var connection = Connect(app, TestTokens.Mint("u1"));
        await connection.StartAsync(Ct);
        var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", room, Ct));

        Assert.Contains("Forbidden", ex.Message);
        Assert.Equal(2, store.MembershipChecks);
        Assert.Empty(app.Services.GetRequiredService<RoomInterestManager>().LiveSubscriptions());
    }

    [Fact]
    public async Task Hub_ConnectionsPerUserAreCapped()
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        var room = Guid.NewGuid();
        store.Members[(room, "u1")] = true;
        var app = OfflineApp.Create(factory, store, b => b.UseSetting("Dotwire:MaxConnectionsPerUser", "1"));

        await using var first = Connect(app, TestTokens.Mint("u1"));
        await first.StartAsync(Ct);
        await first.InvokeAsync("Subscribe", room, Ct);

        await using var second = Connect(app, TestTokens.Mint("u1"));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await second.StartAsync(Ct);
            await second.InvokeAsync("Subscribe", room, Ct);
        });

        Assert.Equal(1, app.Services.GetRequiredService<UserConnectionLimiter>().CountFor("u1"));
    }

    [Fact]
    public async Task Hub_ConnectIsRateLimitedPerUser()
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        var app = OfflineApp.Create(factory, store, b =>
        {
            b.UseSetting("Dotwire:RateLimits:Read:PermitLimit", "1");
            b.UseSetting("Dotwire:RateLimits:Read:TokensPerPeriod", "1");
            b.UseSetting("Dotwire:RateLimits:Read:PeriodSeconds", "3600");
        });

        await using var first = Connect(app, TestTokens.Mint("u1"));
        await first.StartAsync(Ct);
        await using var second = Connect(app, TestTokens.Mint("u1"));

        await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync(Ct));
    }

    [Fact]
    public async Task Sse_RevokedWhileRegistering_Is403()
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        var room = Guid.NewGuid();
        store.MembershipOverride = (_, _, call) => call == 1;
        var app = OfflineApp.Create(factory, store);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint("u1"));

        var response = await client.GetAsync($"/rooms/{room}/events", HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(app.Services.GetRequiredService<RoomInterestManager>().LiveSubscriptions());
        Assert.Equal(0, app.Services.GetRequiredService<UserConnectionLimiter>().CountFor("u1"));
    }

    [Fact]
    public async Task Sse_ConnectionsPerUserAreCapped()
    {
        var store = new FakeAccessStore();
        store.Roles["u1"] = "member";
        var room = Guid.NewGuid();
        store.Members[(room, "u1")] = true;
        var app = OfflineApp.Create(factory, store, b => b.UseSetting("Dotwire:MaxConnectionsPerUser", "1"));
        var limiter = app.Services.GetRequiredService<UserConnectionLimiter>();
        Assert.True(limiter.TryAcquire("u1"));

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint("u1"));
        var response = await client.GetAsync($"/rooms/{room}/events", HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        limiter.Release("u1");
    }
}

public class RealtimeUnitSecurityTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    private static RoomInterestManager CreateInterestManager(INatsConnection? nats = null, bool natsEnabled = false, PresenceCoalescer? presence = null)
    {
        nats ??= DispatchProxy.Create<INatsConnection, RealtimeFanoutUnitTests.CountingNats>();
        var encryption = new EncryptionOptions { ActiveKeyId = "k1" };
        encryption.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var natsOptions = Options.Create(new NatsOptions { Enabled = natsEnabled });
        return new RoomInterestManager(
            nats,
            new RoomHubLifetimeManager(NullLogger<DefaultHubLifetimeManager<RoomHub>>.Instance),
            new MessageCipher(Options.Create(encryption)),
            presence ?? new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = false }), NullLogger<PresenceCoalescer>.Instance),
            natsOptions,
            NullLogger<RoomInterestManager>.Instance);
    }

    [Fact]
    public void TypingThrottle_IsSharedAcrossConnectionsOfOneUser()
    {
        var throttle = new TypingThrottle();
        var now = Stopwatch.GetTimestamp();

        Assert.True(throttle.ShouldPublish("u1", RoomId, true, now));
        Assert.False(throttle.ShouldPublish("u1", RoomId, true, now + 1));
        Assert.True(throttle.ShouldPublish("u2", RoomId, true, now + 1));
        Assert.True(throttle.ShouldPublish("u1", RoomId, false, now + 2));
        Assert.False(throttle.ShouldPublish("u1", RoomId, false, now + 3));
        Assert.False(throttle.ShouldPublish("u1", RoomId, true, now + 4));
        Assert.True(throttle.ShouldPublish("u1", RoomId, true, now + TypingThrottle.CooldownTicks));
    }

    [Fact]
    public void TypingThrottle_PrunesIdleEntries()
    {
        var throttle = new TypingThrottle();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100; i++)
            throttle.ShouldPublish($"u{i}", RoomId, true, start);

        throttle.ShouldPublish("late", RoomId, true, start + Stopwatch.Frequency * 120);

        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public void ConnectionLimiter_CapsAndReleases()
    {
        var limiter = new UserConnectionLimiter(Options.Create(new DotwireOptions { MaxConnectionsPerUser = 2 }));

        Assert.True(limiter.TryAcquire("u1"));
        Assert.True(limiter.TryAcquire("u1"));
        Assert.False(limiter.TryAcquire("u1"));
        Assert.True(limiter.TryAcquire("u2"));
        limiter.Release("u1");
        Assert.True(limiter.TryAcquire("u1"));
        limiter.Release("u1");
        limiter.Release("u1");
        limiter.Release("u1");
        Assert.Equal(0, limiter.CountFor("u1"));
    }

    [Fact]
    public void Presence_IsRefCountedPerUser()
    {
        var nats = DispatchProxy.Create<INatsConnection, RealtimeFanoutUnitTests.CountingNats>();
        var presence = new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = true }), NullLogger<PresenceCoalescer>.Instance);

        presence.QueueJoin(RoomId, "u1");
        presence.QueueJoin(RoomId, "u1");
        presence.QueueLeave(RoomId, "u1");

        Assert.Equal([("u1", true)], presence.PeekQueued(RoomId));

        presence.QueueLeave(RoomId, "u1");
        presence.QueueLeave(RoomId, "u1");

        Assert.Equal([("u1", true), ("u1", false)], presence.PeekQueued(RoomId));
    }

    [Fact]
    public void Presence_QueuesNothingWhenNatsIsDisabled()
    {
        var nats = DispatchProxy.Create<INatsConnection, RealtimeFanoutUnitTests.CountingNats>();
        var presence = new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = false }), NullLogger<PresenceCoalescer>.Instance);

        for (var i = 0; i < 100; i++)
            presence.QueueJoin(Guid.NewGuid(), "u1");

        Assert.Equal(0, presence.PendingRooms);
    }

    private sealed class FakeCaller(string connectionId, string sub) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => sub;
        public override ClaimsPrincipal? User { get; } = new(new ClaimsIdentity([new Claim("sub", sub)], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class DummyHub : Hub
    {
        public void Subscribe(Guid roomId) { }
    }

    [Fact]
    public async Task HubRateLimit_IsSharedAcrossConnectionsOfOneUser()
    {
        var filter = new HubRateLimitFilter(Options.Create(new RateLimitOptions
        {
            Hub = new RateLimitBucketOptions { PermitLimit = 1, TokensPerPeriod = 1, PeriodSeconds = 3600 },
        }));
        var method = typeof(DummyHub).GetMethod(nameof(DummyHub.Subscribe))!;
        var services = new ServiceCollection().BuildServiceProvider();
        HubInvocationContext Invocation(string connectionId) =>
            new(new FakeCaller(connectionId, "u1"), services, new DummyHub(), method, [Guid.NewGuid()]);

        await filter.InvokeMethodAsync(Invocation("c1"), _ => ValueTask.FromResult<object?>(null));
        var ex = await Assert.ThrowsAsync<HubException>(async () =>
            await filter.InvokeMethodAsync(Invocation("c2"), _ => ValueTask.FromResult<object?>(null)));

        Assert.Equal("RateLimited", ex.Message);
    }

    [Fact]
    public async Task Sweeper_RevokesNonMembersAndRoleMismatches()
    {
        var interest = CreateInterestManager();
        var store = new FakeAccessStore();
        var keepRoom = Guid.NewGuid();
        var dropRoom = Guid.NewGuid();
        store.Roles["keep"] = "member";
        store.Roles["demoted"] = "member";
        store.Members[(keepRoom, "keep")] = true;
        store.Members[(keepRoom, "demoted")] = true;

        var (_, keepRevoked) = await interest.RegisterSseConnectionAsync(keepRoom, "sse-keep", "keep", 8, "member");
        var (_, goneRevoked) = await interest.RegisterSseConnectionAsync(dropRoom, "sse-gone", "keep", 8, "member");
        var (_, demotedRevoked) = await interest.RegisterSseConnectionAsync(keepRoom, "sse-demoted", "demoted", 8, "admin");

        var sweeper = new MembershipSweeper(interest, store, Options.Create(new DotwireOptions()), NullLogger<MembershipSweeper>.Instance);
        var revoked = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(2, revoked);
        Assert.False(keepRevoked.IsCancellationRequested);
        Assert.True(goneRevoked.IsCancellationRequested);
        Assert.True(demotedRevoked.IsCancellationRequested);
        Assert.Equal([(keepRoom, "keep", (string?)"member")], interest.LiveSubscriptions());
    }

    [Fact]
    public async Task Sse_EphemeralEventsAreShedBeforeMessages()
    {
        var interest = CreateInterestManager();
        var (reader, _) = await interest.RegisterSseConnectionAsync(RoomId, "sse-1", "u1", bufferSize: 4);

        for (ulong seq = 1; seq <= 3; seq++)
            interest.PublishToSse(RoomId, new SseOutboundEvent("message", [], seq));
        interest.PublishToSse(RoomId, new SseOutboundEvent("typing", [], null), ephemeral: true);
        interest.PublishToSse(RoomId, new SseOutboundEvent("message", [], 4));

        var names = new List<string>();
        while (reader.TryRead(out var evt))
            names.Add(evt.EventName);

        Assert.Equal(["message", "message", "message", "message"], names);
        Assert.False(reader.Completion.IsCompleted);
    }

    public class FakeSub : DispatchProxy
    {
        public Channel<NatsMsg<byte[]>> Channel = System.Threading.Channels.Channel.CreateUnbounded<NatsMsg<byte[]>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Msgs" => Channel.Reader,
            "DisposeAsync" => ValueTask.CompletedTask,
            "UnsubscribeAsync" => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }

    public class SubscribingNats : DispatchProxy
    {
        public readonly List<FakeSub> Subs = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "SubscribeCoreAsync":
                    var sub = DispatchProxy.Create<INatsSub<byte[]>, FakeSub>();
                    lock (Subs)
                        Subs.Add((FakeSub)(object)sub);
                    return ValueTask.FromResult(sub);
                case "PingAsync":
                    return ValueTask.FromResult(TimeSpan.Zero);
                case "PublishAsync":
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    [Fact]
    public async Task DeadFanoutLoop_DropsLocalSubscribersSoTheyResubscribe()
    {
        var nats = DispatchProxy.Create<INatsConnection, SubscribingNats>();
        var interest = CreateInterestManager(nats, natsEnabled: true);
        var (reader, _) = await interest.RegisterSseConnectionAsync(RoomId, "sse-1", "u1", bufferSize: 8);
        Assert.True(interest.HasSubscription(RoomId));

        var subs = ((SubscribingNats)(object)nats).Subs;
        lock (subs)
            subs[0].Channel.Writer.Complete();

        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(interest.HasSubscription(RoomId));
    }

    [Fact]
    public async Task Replay_UndecryptableRowsStillAdvanceTheCursor()
    {
        var pages = 0;
        Task<(List<(ulong, DateTimeOffset, string, string, byte[])>, bool)> Page(ulong after, CancellationToken _)
        {
            pages++;
            var rows = Enumerable.Range(1, 3)
                .Select(i => (after + (ulong)i, DateTimeOffset.UtcNow, "s", "k", Array.Empty<byte>()))
                .ToList();
            return Task.FromResult((rows, after < 9));
        }

        var (last, truncated) = await Events.ReplayAsync(0, 0, Page, (_, _) => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(12UL, last);
        Assert.False(truncated);
        Assert.Equal(4, pages);
    }

    [Fact]
    public async Task Replay_StopsAtTheRowCapAndReportsTruncation()
    {
        Task<(List<(ulong, DateTimeOffset, string, string, byte[])>, bool)> Page(ulong after, CancellationToken _) =>
            Task.FromResult((Enumerable.Range(1, 10).Select(i => (after + (ulong)i, DateTimeOffset.UtcNow, "s", "k", Array.Empty<byte>())).ToList(), true));

        var delivered = 0;
        var (last, truncated) = await Events.ReplayAsync(100, 25, Page, (_, _) => Task.FromResult(++delivered > 0), CancellationToken.None);

        Assert.True(truncated);
        Assert.Equal(25, delivered);
        Assert.Equal(125UL, last);
    }

    [Theory]
    [InlineData("42", 7UL, 42UL)]
    [InlineData("not-a-number", 7UL, 7UL)]
    [InlineData(null, 7UL, 7UL)]
    [InlineData("", null, null)]
    public void ResumeSeq_PrefersAValidLastEventId(string? header, ulong? afterSeq, ulong? expected)
    {
        Assert.Equal(expected, Events.ResolveResumeSeq(header, afterSeq));
    }
}
