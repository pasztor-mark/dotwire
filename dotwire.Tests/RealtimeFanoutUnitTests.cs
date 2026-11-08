using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
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
            NullLogger<RoomInterestManager>.Instance);
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
            Options.Create(new NatsOptions()))
        {
            Context = new FakeCallerContext("conn-1", "user-1"),
        };
        return (hub, counting);
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
