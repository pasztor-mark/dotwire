using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Dotwire.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class AccessLogDsarComposeTests : IAsyncDisposable
{
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public AccessLogDsarComposeTests()
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

    private WebApplicationFactory<Program> NewFactory(Action<IWebHostBuilder>? configure = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
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
            b.UseSetting("Dotwire:RateLimits:Enabled", "false");
            configure?.Invoke(b);
        });
        _factories.Add(factory);
        _ = factory.Services;
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
            await factory.DisposeAsync();
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(CancellationToken.None);
        await using var cmd = new NpgsqlCommand("SELECT set_access_log_retention(0)", conn);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        return conn;
    }

    private async Task SeedUserAsync(string sub, string role, params Guid[] rooms)
    {
        await using var conn = await OpenOwnerAsync();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role) VALUES ($1, $2) ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role", conn))
        {
            cmd.Parameters.AddWithValue(sub);
            cmd.Parameters.AddWithValue(role);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        foreach (var room in rooms)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.AddWithValue(room);
            cmd.Parameters.AddWithValue(sub);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task SeedAccessRowAsync(Guid roomId, string userId, string? actorId)
    {
        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO access_log (time, stream_seq, event, room_id, user_id, actor_id, role, via, count) " +
            "VALUES (now(), $1, 'history.read', $2, $3, $4, 'member', 'rest', 1)", conn);
        cmd.Parameters.AddWithValue(Random.Shared.NextInt64(1, long.MaxValue));
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue((object?)actorId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string sub, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub, role: role));
        return client;
    }

    [Fact]
    public async Task DsarExport_IncludesAccessLog_ForUserAndActor()
    {
        var factory = NewFactory();
        var admin = $"it-al-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");
        var user = $"it-al-user-{Guid.NewGuid():N}";
        var other = $"it-al-other-{Guid.NewGuid():N}";
        var asUser = Guid.NewGuid();
        var asActor = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        await SeedAccessRowAsync(asUser, user, null);
        await SeedAccessRowAsync(asActor, other, user);
        await SeedAccessRowAsync(unrelated, other, other);

        var export = await ClientFor(factory, admin, "admin").GetStringAsync($"/admin/users/{user}/export", Ct);

        using var doc = JsonDocument.Parse(export);
        var entries = doc.RootElement.GetProperty("accessLog").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, e => e.GetProperty("roomId").GetString() == asUser.ToString()
            && e.GetProperty("userId").GetString() == user
            && e.GetProperty("actorId").ValueKind == JsonValueKind.Null);
        Assert.Contains(entries, e => e.GetProperty("roomId").GetString() == asActor.ToString()
            && e.GetProperty("userId").GetString() == other
            && e.GetProperty("actorId").GetString() == user);
        Assert.DoesNotContain(entries, e => e.GetProperty("roomId").GetString() == unrelated.ToString());
    }

    [Fact]
    public async Task Compliance_ReportsAccessLogSettings()
    {
        var factory = NewFactory(b =>
        {
            b.UseSetting("Dotwire:AccessLog:Enabled", "true");
            b.UseSetting("Dotwire:AccessLog:RetentionDays", "2190");
        });
        var admin = $"it-al-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");

        var body = await ClientFor(factory, admin, "admin").GetFromJsonAsync<ComplianceResponse>("/admin/compliance", Ct);

        Assert.NotNull(body);
        Assert.True(body.AccessLogEnabled);
        Assert.Equal(2190, body.AccessLogRetentionDays);
    }
}
