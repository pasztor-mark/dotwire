using System.Net.Sockets;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class JetStreamMetricsPollerComposeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JetStreamMetricsPollerComposeTests()
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", 4222).Wait(TimeSpan.FromSeconds(1)))
                Assert.Skip("NATS not reachable on 127.0.0.1:4222 . run: docker compose up -d nats");
        }
        catch (Exception)
        {
            Assert.Skip("NATS not reachable on 127.0.0.1:4222 . run: docker compose up -d nats");
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        public NatsConnection Connection { get; } = new(new NatsOpts { Url = "nats://127.0.0.1:4222" });
        public NatsJSContext Js { get; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Rooms => $"T_ROOMS_{Id}";
        public string Audit => $"T_AUDIT_{Id}";
        public string RoomSubject => $"t{Id}.r1";
        public string AuditSubject => $"t{Id}a.events";
        public NatsOptions Options => new()
        {
            RoomsStream = Rooms,
            RoomsSubjectFilter = $"t{Id}.*",
            PostgresWriterConsumer = "t-writer",
            AuditStream = Audit,
            AuditSubject = AuditSubject,
            AuditWriterConsumer = "t-audit",
        };

        public Env() => Js = new NatsJSContext(Connection);

        public async Task CreateRoomsAsync()
        {
            await Js.CreateStreamAsync(new StreamConfig(Rooms, [$"t{Id}.*"]) { MaxAge = TimeSpan.FromHours(1) }, Ct);
            await Js.CreateOrUpdateConsumerAsync(Rooms, new ConsumerConfig { Name = "t-writer", DurableName = "t-writer", AckPolicy = ConsumerConfigAckPolicy.Explicit }, Ct);
        }

        public async Task CreateAuditAsync()
        {
            await Js.CreateStreamAsync(new StreamConfig(Audit, [AuditSubject])
            {
                Retention = StreamConfigRetention.Interest,
                MaxBytes = 1024 * 1024,
            }, Ct);
            await Js.CreateOrUpdateConsumerAsync(Audit, new ConsumerConfig { Name = "t-audit", DurableName = "t-audit", AckPolicy = ConsumerConfigAckPolicy.Explicit }, Ct);
        }

        public async Task PublishAsync(string subject, int count)
        {
            for (var i = 0; i < count; i++)
                await Js.PublishAsync(subject, new byte[] { 1, 2, 3 }, cancellationToken: Ct);
        }

        public JetStreamMetricsPoller Poller(DotwireMetrics metrics) =>
            new(Js, Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new Dotwire.Configuration.AccessLogOptions()), metrics, NullLogger<JetStreamMetricsPoller>.Instance);

        public async ValueTask DisposeAsync()
        {
            foreach (var name in new[] { Rooms, Audit })
            {
                try { await Js.DeleteStreamAsync(name, CancellationToken.None); } catch (Exception) { }
            }
            await Connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task PollOnce_ReportsPendingAgeAndLimits()
    {
        await using var env = new Env();
        await env.CreateRoomsAsync();
        await env.CreateAuditAsync();
        await env.PublishAsync(env.RoomSubject, 3);
        await env.PublishAsync(env.AuditSubject, 2);
        await Task.Delay(1100, Ct);
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var poller = env.Poller(metrics);
        metrics.ObserveJetStream(() => poller.Snapshots);

        await poller.PollOnceAsync(Ct);

        var rooms = Assert.Single(poller.Snapshots, s => s.Stream == env.Rooms);
        Assert.Equal(3, rooms.Pending);
        Assert.True(rooms.OldestPendingAgeSeconds >= 1.0);
        Assert.Equal(3600, rooms.MaxAgeSeconds);
        Assert.True(rooms.MaxBytes <= 0);
        Assert.Equal(3, rooms.Messages);
        var audit = Assert.Single(poller.Snapshots, s => s.Stream == env.Audit);
        Assert.Equal(2, audit.Pending);
        Assert.Equal(1048576, audit.MaxBytes);
        Assert.True(audit.Bytes > 0);

        Assert.All(capture.Observe("dotwire.jetstream.stream.max_bytes"), m => Assert.Equal(env.Audit, m.Tags["stream"]));
        Assert.Single(capture.Observe("dotwire.jetstream.stream.max_bytes"));
        Assert.All(capture.Observe("dotwire.jetstream.stream.max_age"), m => Assert.Equal(env.Rooms, m.Tags["stream"]));
        Assert.Single(capture.Observe("dotwire.jetstream.stream.max_age"));
    }

    [Fact]
    public async Task PollOnce_RedactedFloorMessage_StillReportsAge()
    {
        await using var env = new Env();
        await env.CreateRoomsAsync();
        await env.CreateAuditAsync();
        await env.PublishAsync(env.RoomSubject, 3);
        var stream = await env.Js.GetStreamAsync(env.Rooms, cancellationToken: Ct);
        await stream.DeleteMessageAsync(new StreamMsgDeleteRequest { Seq = 1, NoErase = false }, Ct);
        await Task.Delay(1100, Ct);
        var (metrics, _) = TestMetrics.Create();
        var poller = env.Poller(metrics);

        await poller.PollOnceAsync(Ct);

        var rooms = Assert.Single(poller.Snapshots, s => s.Stream == env.Rooms);
        Assert.Equal(2, rooms.Pending);
        Assert.True(rooms.OldestPendingAgeSeconds >= 0.5);
    }

    [Fact]
    public async Task PollOnce_AfterDrain_ReportsZero()
    {
        await using var env = new Env();
        await env.CreateRoomsAsync();
        await env.CreateAuditAsync();
        await env.PublishAsync(env.RoomSubject, 2);
        var consumer = await env.Js.GetConsumerAsync(env.Rooms, "t-writer", Ct);
        var got = 0;
        await foreach (var msg in consumer.FetchAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 2, Expires = TimeSpan.FromSeconds(5) }, cancellationToken: Ct))
        {
            await msg.AckAsync(new AckOpts { DoubleAck = true }, Ct);
            got++;
        }
        Assert.Equal(2, got);
        var (metrics, _) = TestMetrics.Create();
        var poller = env.Poller(metrics);

        await poller.PollOnceAsync(Ct);

        var rooms = Assert.Single(poller.Snapshots, s => s.Stream == env.Rooms);
        Assert.Equal(0, rooms.Pending);
        Assert.Equal(0, rooms.OldestPendingAgeSeconds);
    }

    [Fact]
    public async Task PollOnce_MissingStream_OmitsOnlyThatStream()
    {
        await using var env = new Env();
        await env.CreateRoomsAsync();
        await env.CreateAuditAsync();
        var (metrics, _) = TestMetrics.Create();
        var poller = env.Poller(metrics);
        await poller.PollOnceAsync(Ct);
        Assert.Equal(2, poller.Snapshots.Count);

        await env.Js.DeleteStreamAsync(env.Rooms, Ct);
        await poller.PollOnceAsync(Ct);
        Assert.DoesNotContain(poller.Snapshots, s => s.Stream == env.Rooms);
        Assert.Contains(poller.Snapshots, s => s.Stream == env.Audit);

        await env.CreateRoomsAsync();
        await poller.PollOnceAsync(Ct);
        Assert.Contains(poller.Snapshots, s => s.Stream == env.Rooms);
        Assert.Contains(poller.Snapshots, s => s.Stream == env.Audit);
    }
}
