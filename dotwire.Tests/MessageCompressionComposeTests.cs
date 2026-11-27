using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class MessageCompressionComposeTests
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;

    public MessageCompressionComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        _appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={_appPassword}";
        _baseFactory = new WebApplicationFactory<Program>();
    }

    private WebApplicationFactory<Program> Start(string? afterDays)
    {
        var f = _baseFactory.WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", _appConnectionString);
            b.UseSetting("ConnectionStrings:PostgresRead", _appConnectionString);
            b.UseSetting("Postgres:AppRolePassword", _appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://127.0.0.1:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            if (afterDays is not null) b.UseSetting("Dotwire:Compression:AfterDays", afterDays);
        });
        _ = f.Services;
        return f;
    }

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

    private async Task<NpgsqlConnection> OpenOwnerConnectionAsync()
    {
        var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private async Task<T> ScalarOwnerAsync<T>(string sql, params object[] args)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task ExecOwnerAsync(string sql)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private Task<long> PolicyJobCountAsync() => ScalarOwnerAsync<long>(
        "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_compression' AND hypertable_name = 'messages'");

    private Task<bool> CompressAfterIsAsync(int days) => ScalarOwnerAsync<bool>(
        "SELECT (config->>'compress_after')::interval = make_interval(days => $1) FROM timescaledb_information.jobs WHERE proc_name = 'policy_compression' AND hypertable_name = 'messages'",
        days);

    [Fact]
    public async Task Policy_IsAdded_KeptWhenUnset_AndRemovedByZero()
    {
        try
        {
            using (Start("36500")) { }
            Assert.Equal(1L, await PolicyJobCountAsync());
            Assert.True(await CompressAfterIsAsync(36500));

            using (Start("36500")) { }
            Assert.Equal(1L, await PolicyJobCountAsync());

            using (Start(null)) { }
            Assert.Equal(1L, await PolicyJobCountAsync());

            using (Start("0")) { }
            Assert.Equal(0L, await PolicyJobCountAsync());
        }
        finally
        {
            await ExecOwnerAsync("SELECT remove_compression_policy('messages', if_exists => true)");
            await ExecOwnerAsync("ALTER TABLE messages SET (timescaledb.compress = false)");
            await ExecOwnerAsync("SELECT remove_compression_policy('access_log', if_exists => true)");
            await ExecOwnerAsync("SELECT decompress_chunk(c, if_compressed => true) FROM show_chunks('access_log') c");
            await ExecOwnerAsync("ALTER TABLE access_log SET (timescaledb.compress = false)");
        }
    }

    [Fact]
    public async Task Migration0010_RemovedTimeIndex()
    {
        using (Start(null)) { }
        Assert.Equal(0L, await ScalarOwnerAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'messages' AND indexname = 'messages_time_idx'"));
    }
}
