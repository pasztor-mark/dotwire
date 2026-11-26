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
public sealed class LegalHoldComposeTests : IAsyncDisposable
{
    private const string ActiveHoldCountSql =
        "SELECT count(*) FROM legal_holds h WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)";

    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly List<long> _placed = [];

    public LegalHoldComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        _appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={_appPassword}";
        _factory = NewFactory();
        _ = _factory.Services;
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

    private WebApplicationFactory<Program> NewFactory(Action<IWebHostBuilder>? configure = null)
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
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseHoldsAsync(_placed.ToArray());
        foreach (var factory in _factories)
            await factory.DisposeAsync();
    }

    private HttpClient AdminClient(WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint("host-admin", role: "admin"));
        return client;
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        return conn;
    }

    private async Task<T> OwnerScalarAsync<T>(string sql, params object[] args)
    {
        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task OwnerExecAsync(string sql, params object[] args)
    {
        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task ReleaseHoldsAsync(params long[] ids)
    {
        if (ids.Length == 0)
            return;

        await using var conn = await OpenOwnerAsync();
        await using (var release = new NpgsqlCommand(
            "INSERT INTO legal_hold_releases (hold_id, released_by) " +
            "SELECT h.id, 'test-cleanup' FROM unnest($1::bigint[]) AS h(id) " +
            "WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)", conn))
        {
            release.Parameters.AddWithValue(ids);
            await release.ExecuteNonQueryAsync(Ct);
        }

        await using var sync = new NpgsqlCommand("SELECT sync_retention_schedule()", conn);
        await sync.ExecuteNonQueryAsync(Ct);
    }

    private async Task RestoreRetentionAsync(int days) =>
        await OwnerExecAsync("SELECT set_message_retention($1)", days);

    private async Task<HttpResponseMessage> PostHoldAsync(object body) =>
        await AdminClient().PostAsJsonAsync("/admin/holds", body, Ct);

    private async Task<LegalHold> PlaceRoomHoldAsync(Guid? room = null)
    {
        var response = await PostHoldAsync(new { roomId = room ?? Guid.NewGuid(), matterRef = "matter-" + Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = await response.Content.ReadFromJsonAsync<LegalHold>(Ct);
        Assert.NotNull(hold);
        _placed.Add(hold.Id);
        return hold;
    }

    private async Task<string?> AuditRowJsonAsync(string eventType, long value)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await using var conn = await OpenOwnerAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT row_to_json(a)::text FROM audit_log a WHERE a.event_type = $1 AND a.value = $2 ORDER BY a.id LIMIT 1", conn);
            cmd.Parameters.AddWithValue(eventType);
            cmd.Parameters.AddWithValue(value);
            if (await cmd.ExecuteScalarAsync(Ct) is string json)
                return json;
            await Task.Delay(250, Ct);
        }
        return null;
    }

    [Fact]
    public async Task PlaceRoomHold_Returns201_AndAuditsIdsOnly()
    {
        var room = Guid.NewGuid();
        var matterRef = "matter-" + Guid.NewGuid();
        var response = await PostHoldAsync(new { roomId = room, matterRef });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = await response.Content.ReadFromJsonAsync<LegalHold>(Ct);
        if (hold is not null)
            _placed.Add(hold.Id);

        Assert.NotNull(hold);
        Assert.Equal($"/admin/holds/{hold.Id}", response.Headers.Location?.ToString());
        Assert.Equal("room", hold.Scope);
        Assert.Equal(room, hold.RoomId);
        Assert.Null(hold.UserId);
        Assert.Equal(matterRef, hold.MatterRef);
        Assert.Equal("host-admin", hold.PlacedBy);
        Assert.Null(hold.ReleasedAt);

        var auditJson = await AuditRowJsonAsync("hold.placed", hold.Id);
        Assert.NotNull(auditJson);
        Assert.Contains(room.ToString(), auditJson);
        Assert.DoesNotContain(matterRef, auditJson);
    }

    [Fact]
    public async Task PlaceHold_BothTargets_Is400()
    {
        var response = await PostHoldAsync(new { roomId = Guid.NewGuid(), userId = "user-" + Guid.NewGuid(), matterRef = "m" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct);
        Assert.Equal("invalid_request", body?.Error);
    }

    [Fact]
    public async Task PlaceHold_NoTarget_Is400()
    {
        var response = await PostHoldAsync(new { matterRef = "m" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct);
        Assert.Equal("invalid_request", body?.Error);
    }

    [Fact]
    public async Task Release_Twice_Is409AlreadyReleased()
    {
        var hold = await PlaceRoomHoldAsync();
        try
        {
            var first = await AdminClient().DeleteAsync($"/admin/holds/{hold.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

            var second = await AdminClient().DeleteAsync($"/admin/holds/{hold.Id}", Ct);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var body = await second.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct);
            Assert.Equal("already_released", body?.Error);
        }
        finally
        {
            await ReleaseHoldsAsync(hold.Id);
        }
    }

    [Fact]
    public async Task Release_Unknown_Is404()
    {
        var response = await AdminClient().DeleteAsync($"/admin/holds/{long.MaxValue}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_ActiveOnly_ExcludesReleased()
    {
        var released = await PlaceRoomHoldAsync();
        var active = await PlaceRoomHoldAsync();
        try
        {
            var delete = await AdminClient().DeleteAsync($"/admin/holds/{released.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            var activeIds = (await AdminClient().GetFromJsonAsync<LegalHold[]>("/admin/holds", Ct))!.Select(h => h.Id).ToArray();
            Assert.Contains(active.Id, activeIds);
            Assert.DoesNotContain(released.Id, activeIds);

            var inactiveIds = (await AdminClient().GetFromJsonAsync<LegalHold[]>("/admin/holds?active=false", Ct))!.Select(h => h.Id).ToArray();
            Assert.Contains(released.Id, inactiveIds);
            Assert.DoesNotContain(active.Id, inactiveIds);
        }
        finally
        {
            await ReleaseHoldsAsync(released.Id, active.Id);
        }
    }

    [Fact]
    public async Task RetentionPut_WhileHeld_ReportsPaused()
    {
        var original = await OwnerScalarAsync<int>("SELECT get_message_retention()");
        var hold = await PlaceRoomHoldAsync();
        try
        {
            var put = await AdminClient().PutAsJsonAsync("/admin/retention", new { messagesDays = 30 }, Ct);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var putBody = await put.Content.ReadFromJsonAsync<RetentionResponse>(Ct);
            Assert.Equal(30, putBody?.MessagesDays);
            Assert.True(putBody?.Paused);

            var release = await AdminClient().DeleteAsync($"/admin/holds/{hold.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);

            var get = await AdminClient().GetFromJsonAsync<RetentionResponse>("/admin/retention", Ct);
            Assert.Equal(30, get?.MessagesDays);
            Assert.False(get?.Paused);
        }
        finally
        {
            await ReleaseHoldsAsync(hold.Id);
            await RestoreRetentionAsync(original);
        }
    }

    [Fact]
    public async Task StartupRetention_WhileHeld_StaysPaused()
    {
        var original = await OwnerScalarAsync<int>("SELECT get_message_retention()");
        var hold = await PlaceRoomHoldAsync();
        try
        {
            var startup = NewFactory(b => b.UseSetting("Dotwire:Retention:MessagesDays", "30"));
            _ = startup.Services;

            Assert.Equal(30, await OwnerScalarAsync<int>("SELECT get_message_retention()"));
            Assert.True(await OwnerScalarAsync<bool>("SELECT get_retention_paused()"));
        }
        finally
        {
            await ReleaseHoldsAsync(hold.Id);
            await RestoreRetentionAsync(original);
        }
    }

    [Fact]
    public async Task RetentionPut_BelowMinimumKeep_Is409()
    {
        var strict = NewFactory(b => b.UseSetting("Dotwire:Compliance:MinimumKeepDays", "365"));
        _ = strict.Services;

        var response = await AdminClient(strict).PutAsJsonAsync("/admin/retention", new { messagesDays = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct);
        Assert.Equal("minimum_keep", body?.Error);
    }

    [Fact]
    public async Task Compliance_ReturnsEffectiveSettings()
    {
        var hold = await PlaceRoomHoldAsync();
        try
        {
            var expectedActive = await OwnerScalarAsync<long>(ActiveHoldCountSql);
            var body = await AdminClient().GetFromJsonAsync<ComplianceResponse>("/admin/compliance", Ct);

            Assert.NotNull(body);
            Assert.True(body.AllowChanges);
            Assert.Equal(0, body.MinimumKeepDays);
            Assert.Equal("anonymize", body.ErasureMode);
            Assert.Equal(expectedActive, body.ActiveHolds);
        }
        finally
        {
            await ReleaseHoldsAsync(hold.Id);
        }
    }
}
