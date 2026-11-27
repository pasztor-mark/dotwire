using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class AccessLogSqlComposeTests : IAsyncDisposable
{
    private const string RetentionJobCountSql =
        "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' AND hypertable_name = 'access_log'";
    private const string RetentionScheduledSql =
        "SELECT scheduled FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' AND hypertable_name = 'access_log'";

    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly List<long> _placed = [];

    public AccessLogSqlComposeTests()
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

    private WebApplicationFactory<Program> Start(Action<IWebHostBuilder>? configure = null)
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
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            configure?.Invoke(b);
        });
        _factories.Add(factory);
        _ = factory.Services;
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _placed)
            await ExecAsync(_ownerConnectionString,
                "INSERT INTO legal_hold_releases (hold_id, released_by) SELECT $1, 'test-cleanup' WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = $1)", id);
        if (_placed.Count > 0)
            await ExecAsync(_ownerConnectionString, "SELECT sync_retention_schedule()");
        foreach (var factory in _factories)
            await factory.DisposeAsync();
        foreach (var sql in new[]
                 {
                     "SELECT set_access_log_retention(0)",
                     "SELECT remove_compression_policy('access_log', if_exists => true)",
                     "SELECT decompress_chunk(c, if_compressed => true) FROM show_chunks('access_log') c",
                     "ALTER TABLE access_log SET (timescaledb.compress = false)",
                     "SELECT remove_compression_policy('messages', if_exists => true)",
                     "SELECT decompress_chunk(c, if_compressed => true) FROM show_chunks('messages') c",
                     "ALTER TABLE messages SET (timescaledb.compress = false)",
                 })
            await ExecAsync(_ownerConnectionString, sql);
    }

    private async Task<T> ScalarAsync<T>(string cs, string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecAsync(string cs, string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private Task InsertAsync(string cs, Guid room, long seq, string eventName = "history.read", object? expiresAt = null, string? onConflict = null)
    {
        var window = eventName == "history.read" ? (object)DBNull.Value : Guid.NewGuid();
        return ExecAsync(cs,
            "INSERT INTO access_log (time, stream_seq, event, room_id, user_id, role, via, window_id, expires_at, reason, count) " +
            "VALUES ($1, $2, $3, $4, 'u', 'member', 'rest', $5, $6, $7, $8) " + onConflict,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), seq, eventName, room, window,
            expiresAt ?? DBNull.Value,
            eventName == "live.closed" ? "left" : DBNull.Value,
            eventName == "history.read" ? 0 : DBNull.Value);
    }

    [Fact]
    public async Task AppRole_CanInsertAndSelect_CannotMutate()
    {
        Start();
        var room = Guid.NewGuid();
        var seq = Random.Shared.NextInt64(1, long.MaxValue);
        await InsertAsync(_appConnectionString, room, seq);
        Assert.Equal(1L, await ScalarAsync<long>(_appConnectionString, "SELECT count(*) FROM access_log WHERE room_id = $1", room));

        foreach (var sql in new[]
                 {
                     "UPDATE access_log SET role = 'admin' WHERE room_id = $1",
                     "DELETE FROM access_log WHERE room_id = $1",
                     "TRUNCATE access_log",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(_appConnectionString, sql, room));
            Assert.Contains("permission denied", ex.Message);
        }

        var drop = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecAsync(_appConnectionString, "SELECT drop_chunks('access_log', older_than => interval '1 day')"));
        Assert.Contains("must be owner", drop.Message);

        var owner = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecAsync(_ownerConnectionString, "DELETE FROM access_log WHERE room_id = $1", room));
        Assert.Contains("is append-only", owner.Message);
    }

    [Fact]
    public async Task Dedupe_IgnoresSameTimeAndStreamSeq()
    {
        Start();
        var room = Guid.NewGuid();
        var seq = Random.Shared.NextInt64(1, long.MaxValue);
        await InsertAsync(_appConnectionString, room, seq, onConflict: "ON CONFLICT DO NOTHING");
        await InsertAsync(_appConnectionString, room, seq, onConflict: "ON CONFLICT DO NOTHING");
        Assert.Equal(1L, await ScalarAsync<long>(_appConnectionString, "SELECT count(*) FROM access_log WHERE room_id = $1", room));
    }

    [Fact]
    public async Task CheckConstraints_RejectIncompleteOpen()
    {
        Start();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(_appConnectionString, Guid.NewGuid(), Random.Shared.NextInt64(1, long.MaxValue), "live.opened"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        await InsertAsync(_appConnectionString, Guid.NewGuid(), Random.Shared.NextInt64(1, long.MaxValue), "live.opened",
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RetentionZero_RemovesPolicy()
    {
        using (Start(b => b.UseSetting("Dotwire:AccessLog:RetentionDays", "2190"))) { }
        Assert.Equal(1L, await ScalarAsync<long>(_ownerConnectionString, RetentionJobCountSql));

        using (Start(b => b.UseSetting("Dotwire:AccessLog:RetentionDays", "0"))) { }
        Assert.Equal(0L, await ScalarAsync<long>(_ownerConnectionString, RetentionJobCountSql));
    }

    [Fact]
    public async Task Hold_PausesAccessLogRetention_AcrossStartup()
    {
        await ExecAsync(_ownerConnectionString, "SELECT set_message_retention(0)");
        var factory = Start();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint("host-admin", role: "admin"));
        var placed = await client.PostAsJsonAsync("/admin/holds", new { roomId = Guid.NewGuid(), matterRef = "m-" + Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var hold = await placed.Content.ReadFromJsonAsync<LegalHold>(Ct);
        Assert.NotNull(hold);
        _placed.Add(hold.Id);
        Assert.Equal(1L, await ScalarAsync<long>(_ownerConnectionString,
            "SELECT count(*) FROM legal_holds h WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)"));

        using (Start(b => b.UseSetting("Dotwire:AccessLog:RetentionDays", "2190"))) { }
        Assert.Equal(1L, await ScalarAsync<long>(_ownerConnectionString, RetentionJobCountSql));
        Assert.False(await ScalarAsync<bool>(_ownerConnectionString, RetentionScheduledSql));
        Assert.True(await ScalarAsync<bool>(_ownerConnectionString, "SELECT get_retention_paused()"));
        var compliance = await client.GetFromJsonAsync<ComplianceResponse>("/admin/compliance", Ct);
        Assert.True(compliance!.RetentionPaused);

        var released = await client.DeleteAsync($"/admin/holds/{hold.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.True(await ScalarAsync<bool>(_ownerConnectionString, RetentionScheduledSql));
        Assert.False(await ScalarAsync<bool>(_ownerConnectionString, "SELECT get_retention_paused()"));
    }

    [Fact]
    public async Task Compression_AppliesToAccessLog()
    {
        using (Start(b => b.UseSetting("Dotwire:Compression:AfterDays", "36500"))) { }
        Assert.True(await ScalarAsync<bool>(_ownerConnectionString,
            "SELECT compression_enabled FROM timescaledb_information.hypertables WHERE hypertable_name = 'access_log'"));
        Assert.Equal(1L, await ScalarAsync<long>(_ownerConnectionString,
            "SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_compression' AND hypertable_name = 'access_log'"));
        Assert.Equal("room_id", await ScalarAsync<string>(_ownerConnectionString,
            "SELECT segmentby FROM timescaledb_information.hypertable_compression_settings WHERE hypertable = 'access_log'::regclass"));
        Assert.Contains("stream_seq", await ScalarAsync<string>(_ownerConnectionString,
            "SELECT orderby FROM timescaledb_information.hypertable_compression_settings WHERE hypertable = 'access_log'::regclass"));
    }
}
