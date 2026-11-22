using Dotwire.Configuration;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Npgsql;
using System.Net.Sockets;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class AuditWriterPingComposeTests
{
    private const string LockPidSql =
        "SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND classid = 1635083369 AND objid = 1952413554 AND objsubid = 1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, TimeSpan timeout) where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } v)
                return v;
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException("condition not met");
    }

    private static async Task<(AuditWriterService Writer, MetricCapture Capture, NpgsqlDataSource Ds, NatsConnection Nats, string Migrator)> StartAsync()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        var migrator = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";

        await MigrationRunner.RunAsync(migrator, appPassword, NullLogger.Instance, Ct);

        var nats = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:4222" });
        var js = new NatsJSContext(nats);
        var options = new NatsOptions { Enabled = true };
        await JetStreamProvisioner.ProvisionAsync(js, options, NullLogger.Instance, Ct);

        var ds = NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
        var (metrics, scope) = TestMetrics.Create();
        var capture = new MetricCapture(scope);
        var writer = new AuditWriterService(js, Options.Create(options), ds, metrics, NullLogger<AuditWriterService>.Instance)
        {
            IdlePingInterval = TimeSpan.FromSeconds(2),
        };
        await writer.StartAsync(Ct);
        return (writer, capture, ds, nats, migrator);
    }

    private static async Task<int?> HolderPidAsync(string migrator)
    {
        await using var conn = new NpgsqlConnection(migrator);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(LockPidSql, conn);
        return await cmd.ExecuteScalarAsync(Ct) as int?;
    }

    private static bool LockHeld(MetricCapture capture) =>
        capture.Observe("dotwire.audit.writer.lock_held") is [{ Value: 1 }];

    [Fact]
    public async Task IdleWriter_DetectsKilledSession_AndReacquires()
    {
        var (writer, capture, ds, nats, migrator) = await StartAsync();
        try
        {
            await WaitAsync<bool>(() => Task.FromResult<bool?>(LockHeld(capture) ? true : null), TimeSpan.FromSeconds(15));
            var oldPid = await WaitAsync<int>(() => HolderPidAsync(migrator), TimeSpan.FromSeconds(15));

            await using (var conn = new NpgsqlConnection(migrator))
            {
                await conn.OpenAsync(Ct);
                await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", conn);
                kill.Parameters.AddWithValue("pid", oldPid);
                await kill.ExecuteScalarAsync(Ct);
            }

            var newPid = await WaitAsync<int>(async () =>
                await HolderPidAsync(migrator) is { } p && p != oldPid ? p : null, TimeSpan.FromSeconds(15));
            Assert.NotEqual(oldPid, newPid);
            await WaitAsync<bool>(() => Task.FromResult<bool?>(LockHeld(capture) ? true : null), TimeSpan.FromSeconds(15));
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            capture.Dispose();
            await ds.DisposeAsync();
            await nats.DisposeAsync();
        }
    }

    [Fact]
    public async Task StoppedWriter_ReportsZero()
    {
        var (writer, capture, ds, nats, _) = await StartAsync();
        try
        {
            await WaitAsync<bool>(() => Task.FromResult<bool?>(LockHeld(capture) ? true : null), TimeSpan.FromSeconds(15));
            await writer.StopAsync(CancellationToken.None);
            Assert.Equal(0, Assert.Single(capture.Observe("dotwire.audit.writer.lock_held")).Value);
        }
        finally
        {
            capture.Dispose();
            await ds.DisposeAsync();
            await nats.DisposeAsync();
        }
    }
}
