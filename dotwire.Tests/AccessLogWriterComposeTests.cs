using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class AccessLogWriterComposeTests : IAsyncDisposable
{
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private INatsJSContext? _js;

    public AccessLogWriterComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        _appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={_appPassword}";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Stream => $"T_ACCESS_{_id}";

    private string Subject => $"t{_id}.access";

    private static void SkipUnlessReachable(string host, int port, string name)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(1)))
                Assert.Skip($"{name} not reachable on {host}:{port} . run: docker compose up -d postgres nats");
        }
        catch (Exception)
        {
            Assert.Skip($"{name} not reachable on {host}:{port} . run: docker compose up -d postgres nats");
        }
    }

    private WebApplicationFactory<Program> Start(string role = "All", bool enabled = true, long? maxBytes = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", _appConnectionString);
            b.UseSetting("ConnectionStrings:PostgresRead", _appConnectionString);
            b.UseSetting("Postgres:AppRolePassword", _appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://127.0.0.1:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Nats:AccessLogStream", Stream);
            b.UseSetting("Nats:AccessLogSubject", Subject);
            if (maxBytes is not null)
                b.UseSetting("Nats:AccessLogMaxBytes", maxBytes.Value.ToString());
            b.UseSetting("Dotwire:Role", role);
            b.UseSetting("Dotwire:AccessLog:Enabled", enabled ? "true" : "false");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });
        _factories.Add(factory);
        _ = factory.Services;
        _js = factory.Services.GetRequiredService<INatsJSContext>();
        return factory;
    }

    private static AccessEvent Read(Guid room) => new(
        AccessEvents.HistoryRead, DateTimeOffset.UtcNow, room, "u1", null, "member", AccessEvents.Rest, Count: 0);

    private async Task<long> RowsAsync(Guid room, string? eventName = null)
    {
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM access_log WHERE room_id = $1 AND ($2::text IS NULL OR event = $2)", conn);
        cmd.Parameters.AddWithValue(room);
        cmd.Parameters.AddWithValue((object?)eventName ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(100, Ct);
        }

        return await condition();
    }

    private async Task<long> PendingAsync(INatsJSContext js)
    {
        var consumer = await js.GetConsumerAsync(Stream, "access-writer", Ct);
        return (long)(consumer.Info.NumPending + (ulong)consumer.Info.NumAckPending);
    }

    [Fact]
    public async Task Writes_PublishedEvents()
    {
        var factory = Start();
        var publisher = factory.Services.GetRequiredService<AccessLogPublisher>();
        var room = Guid.NewGuid();

        foreach (var chunk in Enumerable.Range(0, 1000).Chunk(100))
            await Task.WhenAll(chunk.Select(_ => publisher.RecordAsync(Read(room), Ct)));

        Assert.True(await WaitAsync(async () => await RowsAsync(room) == 1000, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Redelivery_DoesNotDuplicate()
    {
        var factory = Start(role: "Gateway");
        var js = factory.Services.GetRequiredService<INatsJSContext>();
        var publisher = factory.Services.GetRequiredService<AccessLogPublisher>();
        var room = Guid.NewGuid();
        await js.CreateOrUpdateConsumerAsync(Stream, new ConsumerConfig
        {
            Name = "access-writer",
            DurableName = "access-writer",
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(1),
        }, Ct);
        for (var i = 0; i < 10; i++)
            await publisher.RecordAsync(Read(room), Ct);

        var consumer = await js.GetConsumerAsync(Stream, "access-writer", Ct);
        var batch = new List<INatsJSMsg<byte[]>>();
        await foreach (var msg in consumer.FetchAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 10, Expires = TimeSpan.FromSeconds(2) }, cancellationToken: Ct))
            batch.Add(msg);
        Assert.Equal(10, batch.Count);

        var writer = new AccessLogWriterService(
            js, factory.Services.GetRequiredService<IOptions<NatsOptions>>(),
            factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(PostgresDataSources.Write),
            TestMetrics.Create().Metrics,
            factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AccessLogWriterService>());
        await writer.InsertBatchAsync(batch, Ct);
        Assert.Equal(10, await RowsAsync(room));

        await writer.StartAsync(Ct);
        try
        {
            Assert.True(await WaitAsync(async () => await PendingAsync(js) == 0, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }

        Assert.Equal(10, await RowsAsync(room));
    }

    [Fact]
    public async Task MsgId_DedupesRetriedClose()
    {
        var factory = Start();
        var publisher = factory.Services.GetRequiredService<AccessLogPublisher>();
        var room = Guid.NewGuid();
        var opened = Read(room) with
        {
            Event = AccessEvents.Closed, Via = AccessEvents.Hub, WindowId = Guid.NewGuid(), Reason = AccessEvents.Left,
        };
        publisher.RecordClose(opened);
        publisher.RecordClose(opened);

        Assert.True(await WaitAsync(async () => await RowsAsync(room, "live.closed") >= 1, TimeSpan.FromSeconds(10)));
        await Task.Delay(2000, Ct);
        Assert.Equal(1, await RowsAsync(room, "live.closed"));
    }

    [Fact]
    public async Task Malformed_CountedAndAcked()
    {
        var factory = Start();
        using var capture = new MetricCapture(factory.Services.GetRequiredService<IMeterFactory>());
        var js = factory.Services.GetRequiredService<INatsJSContext>();

        var ack = await js.PublishAsync<byte[]>(Subject, "{\"event\":\"nope\"}"u8.ToArray(), cancellationToken: Ct);
        Assert.Null(ack.Error);

        Assert.True(await WaitAsync(() => Task.FromResult(capture.Sum("dotwire.access_log.malformed") == 1), TimeSpan.FromSeconds(10)));
        Assert.True(await WaitAsync(async () => await PendingAsync(js) == 0, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, capture.Sum("dotwire.access_log.malformed"));
    }

    [Fact]
    public async Task StreamFull_RefusesRecord()
    {
        var factory = Start(role: "Gateway", maxBytes: 1024);
        var publisher = factory.Services.GetRequiredService<AccessLogPublisher>();
        var room = Guid.NewGuid();

        await Assert.ThrowsAsync<AccessLogUnavailableException>(async () =>
        {
            for (var i = 0; i < 100; i++)
                await publisher.RecordAsync(Read(room), Ct);
        });
    }

    [Fact]
    public async Task WriterOnApiNode_DrainsStreamProvisionedByGateway()
    {
        var gateway = Start(role: "Gateway");
        var publisher = gateway.Services.GetRequiredService<AccessLogPublisher>();
        var room = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await publisher.RecordAsync(Read(room), Ct);
        Assert.Equal(0, await RowsAsync(room));

        Start(role: "Api", enabled: false);

        Assert.True(await WaitAsync(async () => await RowsAsync(room) == 5, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task StartupConfigEvent_Chained()
    {
        Start();

        Assert.True(await WaitAsync(async () =>
        {
            await using var conn = new NpgsqlConnection(_ownerConnectionString);
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE event_type = 'access_log.config' AND value = 1 AND event_time >= $1", conn);
            cmd.Parameters.AddWithValue(_started.AddSeconds(-5).UtcDateTime);
            return (long)(await cmd.ExecuteScalarAsync(Ct))! >= 1;
        }, TimeSpan.FromSeconds(15)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
            await factory.DisposeAsync();
        await using var connection = new NATS.Client.Core.NatsConnection(new NATS.Client.Core.NatsOpts { Url = "nats://127.0.0.1:4222" });
        try
        {
            await new NatsJSContext(connection).DeleteStreamAsync(Stream);
        }
        catch (NatsJSApiException)
        {
        }
    }
}
