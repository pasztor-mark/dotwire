using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Xunit;

namespace dotwire.Tests;

public class RealtimeFanoutUnitTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    public class CountingNats : DispatchProxy
    {
        public int Published;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "PublishAsync")
            {
                Interlocked.Increment(ref Published);
                return ValueTask.CompletedTask;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class FakeCallerContext(string connectionId, string userId) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; } = new(new ClaimsIdentity([new Claim("sub", userId)], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private static RoomInterestManager CreateInterestManager(INatsConnection nats)
    {
        var encryption = new EncryptionOptions { ActiveKeyId = "k1" };
        encryption.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var disabled = Options.Create(new NatsOptions { Enabled = false });
        return new RoomInterestManager(
            nats,
            new RoomHubLifetimeManager(NullLogger<DefaultHubLifetimeManager<RoomHub>>.Instance),
            new MessageCipher(Options.Create(encryption)),
            new PresenceCoalescer(nats, disabled, NullLogger<PresenceCoalescer>.Instance),
            disabled,
            NullLogger<RoomInterestManager>.Instance,
            TestMetrics.Create().Metrics);
    }

    private static async Task<(RoomHub Hub, CountingNats Nats)> CreateSubscribedHubAsync()
    {
        var nats = DispatchProxy.Create<INatsConnection, CountingNats>();
        var counting = (CountingNats)(object)nats;
        var interestManager = CreateInterestManager(nats);
        await interestManager.SubscribeAsync(RoomId, "conn-1", "user-1");

        var hub = new RoomHub(
            new FakeAccessStore(),
            interestManager,
            new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = false }), NullLogger<PresenceCoalescer>.Instance),
            new TypingThrottle(),
            new UserConnectionLimiter(Options.Create(new DotwireOptions())),
            nats,
            Options.Create(new NatsOptions()),
            null!,
            null!,
            Options.Create(new DotwireOptions()),
            TestMetrics.Create().Metrics)
        {
            Context = new FakeCallerContext("conn-1", "user-1"),
        };
        return (hub, counting);
    }

    [Fact]
    public async Task Send_RateLimitedRecordsHubRejection()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var nats = DispatchProxy.Create<INatsConnection, CountingNats>();
        var disabled = Options.Create(new NatsOptions { Enabled = false });
        var sendLimiter = new SendRateLimiter(Options.Create(new RateLimitOptions
        {
            Send = new RateLimitBucketOptions { PermitLimit = 1, TokensPerPeriod = 1, PeriodSeconds = 3600 },
        }));
        Assert.True(sendLimiter.TryAcquire("user-1", out _));
        var hub = new RoomHub(
            new FakeAccessStore(),
            CreateInterestManager(nats),
            new PresenceCoalescer(nats, disabled, NullLogger<PresenceCoalescer>.Instance),
            new TypingThrottle(),
            new UserConnectionLimiter(Options.Create(new DotwireOptions())),
            nats,
            Options.Create(new NatsOptions()),
            null!,
            sendLimiter,
            Options.Create(new DotwireOptions { HubSend = new HubSendOptions { Enabled = true } }),
            metrics)
        {
            Context = new FakeCallerContext("conn-1", "user-1"),
        };

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.Send(RoomId, "hi", null));

        Assert.Equal("RateLimited", ex.Message);
        Assert.Equal(1, capture.Sum("dotwire.rejections", ("transport", "hub"), ("reason", "rate_limited"), ("operation", "send")));
    }

    [Fact]
    public async Task RepeatedStopTyping_IsNotPublished()
    {
        var (hub, nats) = await CreateSubscribedHubAsync();

        for (var i = 0; i < 1000; i++)
            await hub.Typing(RoomId, false);

        Assert.Equal(0, nats.Published);
    }

    [Fact]
    public async Task AlternatingTyping_IsBoundedByTheCooldown()
    {
        var (hub, nats) = await CreateSubscribedHubAsync();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 1000; i++)
        {
            await hub.Typing(RoomId, true);
            await hub.Typing(RoomId, false);
        }

        var cooldowns = (int)(elapsed.Elapsed / TimeSpan.FromMilliseconds(500)) + 1;
        Assert.InRange(nats.Published, 2, 2 * cooldowns);
    }

    public class SubjectSubscribingNats : DispatchProxy
    {
        public readonly Dictionary<string, Channel<NatsMsg<byte[]>>> Subjects = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "SubscribeCoreAsync":
                    var subject = (string)args![0]!;
                    var channel = Channel.CreateUnbounded<NatsMsg<byte[]>>();
                    lock (Subjects)
                        Subjects[subject] = channel;
                    var sub = DispatchProxy.Create<INatsSub<byte[]>, FakeSubject>();
                    ((FakeSubject)(object)sub).Channel = channel;
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

    public class FakeSubject : DispatchProxy
    {
        public Channel<NatsMsg<byte[]>> Channel = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Msgs" => Channel.Reader,
            "DisposeAsync" => ValueTask.CompletedTask,
            "UnsubscribeAsync" => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }

    [Fact]
    public async Task ErasedEvent_RelaysToSseAsErasedEvent()
    {
        var nats = DispatchProxy.Create<INatsConnection, SubjectSubscribingNats>();
        var counting = (SubjectSubscribingNats)(object)nats;
        var interestManager = new RoomInterestManager(
            nats,
            new RoomHubLifetimeManager(NullLogger<DefaultHubLifetimeManager<RoomHub>>.Instance),
            new MessageCipher(Options.Create(new EncryptionOptions { ActiveKeyId = "k1", Keys = { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } })),
            new PresenceCoalescer(nats, Options.Create(new NatsOptions { Enabled = false }), NullLogger<PresenceCoalescer>.Instance),
            Options.Create(new NatsOptions { Enabled = true }),
            NullLogger<RoomInterestManager>.Instance,
            TestMetrics.Create().Metrics);
        var (reader, _) = await interestManager.RegisterSseConnectionAsync(RoomId, "sse-1", "user-1", bufferSize: 8);

        var ephemeral = $"room.{RoomId}.ephemeral.>";
        Channel<NatsMsg<byte[]>> channel;
        lock (counting.Subjects)
            channel = counting.Subjects[ephemeral];
        var data = JsonSerializer.SerializeToUtf8Bytes(new MessagesErased(RoomId, "anonymize", [5UL, 6UL]), DotwireJsonContext.Default.MessagesErased);
        await channel.Writer.WriteAsync(new NatsMsg<byte[]>($"room.{RoomId}.ephemeral.erased", null, 0, null, data, null, default), TestContext.Current.CancellationToken);

        var evt = await reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("erased", evt.EventName);
        Assert.Equal(data, evt.Data);
    }

    [Fact]
    public async Task OverflowingSseChannel_DisconnectsInsteadOfDropping()
    {
        var nats = DispatchProxy.Create<INatsConnection, CountingNats>();
        var interestManager = CreateInterestManager(nats);
        var (reader, _) = await interestManager.RegisterSseConnectionAsync(RoomId, "sse-1", "user-1", bufferSize: 1);

        interestManager.PublishToSse(RoomId, new SseOutboundEvent("message", [], 1));
        interestManager.PublishToSse(RoomId, new SseOutboundEvent("message", [], 2));

        var delivered = new List<ulong?>();
        await foreach (var evt in reader.ReadAllAsync(TestContext.Current.CancellationToken))
            delivered.Add(evt.Id);

        Assert.Equal([1UL], delivered);
        Assert.True(reader.Completion.IsCompleted);
    }
}
