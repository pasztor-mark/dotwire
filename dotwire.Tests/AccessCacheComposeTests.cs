using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Dotwire.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class AccessCacheComposeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _ownerConnectionString;

    public AccessCacheComposeTests(WebApplicationFactory<Program> factory)
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        var appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}";

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", appConnectionString);
            b.UseSetting("ConnectionStrings:PostgresRead", appConnectionString);
            b.UseSetting("Postgres:AppRolePassword", appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://127.0.0.1:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            b.UseSetting("Dotwire:RateLimits:Enabled", "false");
            b.UseSetting("Dotwire:AuthCacheSeconds", "30");
            b.UseSetting("Dotwire:AuthBatching:Enabled", "true");
        });
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

    private async Task SeedAsync(string sub, string role, params Guid[] rooms)
    {
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
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

    private HttpClient Client(string sub, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub, role));
        return client;
    }

    private Task<HttpResponseMessage> SendAsync(HttpClient client, Guid room) =>
        client.PostAsJsonAsync($"/rooms/{room}/messages", new { content = "hi" }, Ct);

    private async Task<HttpStatusCode> PollUntilAsync(HttpClient client, Guid room, HttpStatusCode expected, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        HttpStatusCode last;
        do
        {
            last = (await SendAsync(client, room)).StatusCode;
            if (last == expected) return last;
            await Task.Delay(50, Ct);
        } while (DateTime.UtcNow < deadline);
        return last;
    }

    private async Task WaitUsableAsync()
    {
        var cache = _factory.Services.GetRequiredService<AccessCache>();
        for (var i = 0; i < 100 && !cache.IsUsable; i++)
            await Task.Delay(50, Ct);
        Assert.True(cache.IsUsable);
    }

    [Fact]
    public async Task CacheBecomesUsable_AfterSubscription()
    {
        await WaitUsableAsync();
    }

    [Fact]
    public async Task MemberRemovedViaAdminApi_NextSendIs403()
    {
        await WaitUsableAsync();
        var room = Guid.NewGuid();
        var member = $"it-ac-{Guid.NewGuid():N}";
        var admin = $"it-ac-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        await SeedAsync(admin, "admin");
        var memberClient = Client(member, "member");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(memberClient, room)).StatusCode);
        Assert.True(_factory.Services.GetRequiredService<AccessCache>().TryGet(room, member, out _));

        var removed = await Client(admin, "admin").DeleteAsync($"/admin/rooms/{room}/members/{member}", Ct);
        Assert.True(removed.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, await PollUntilAsync(memberClient, room, HttpStatusCode.Forbidden, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RoleSetViaAdminApi_EvictsCachedEntry()
    {
        await WaitUsableAsync();
        var room = Guid.NewGuid();
        var member = $"it-ac-{Guid.NewGuid():N}";
        var admin = $"it-ac-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        await SeedAsync(admin, "admin");
        var memberClient = Client(member, "member");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(memberClient, room)).StatusCode);
        Assert.True(_factory.Services.GetRequiredService<AccessCache>().TryGet(room, member, out _));

        var set = await Client(admin, "admin").PutAsJsonAsync($"/admin/users/{member}/role", new SetUserRoleRequest("auditor"), Ct);
        Assert.True(set.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, await PollUntilAsync(memberClient, room, HttpStatusCode.Forbidden, TimeSpan.FromSeconds(2)));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25, Ct);
        Assert.True(condition());
    }

    private static string ComposeDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "compose.yaml")))
                return dir.FullName;
        Assert.Skip("compose.yaml not found above the test output directory");
        return "";
    }

    private static async Task ComposeAsync(string dir, string verb)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", ["compose", verb, "nats"])
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("docker CLI not available");
            return;
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"docker compose {verb} nats failed: {await process.StandardError.ReadToEndAsync(Ct)}");
        }
    }

    [Fact]
    public async Task NatsOutage_SuspendsCache_AndNoStaleAllowAfterReconnect()
    {
        var dir = ComposeDir();
        await WaitUsableAsync();
        var cache = _factory.Services.GetRequiredService<AccessCache>();
        var room = Guid.NewGuid();
        var member = $"it-ac-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        var memberClient = Client(member, "member");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(memberClient, room)).StatusCode);
        Assert.True(cache.TryGet(room, member, out _));

        await using (var conn = new NpgsqlConnection(_ownerConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand("DELETE FROM room_members WHERE room_id = $1 AND user_id = $2", conn);
            cmd.Parameters.AddWithValue(room);
            cmd.Parameters.AddWithValue(member);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(Ct));
        }
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(memberClient, room)).StatusCode);

        try
        {
            await ComposeAsync(dir, "stop");
            await WaitForAsync(() => !cache.IsUsable, TimeSpan.FromSeconds(5));
            Assert.False(cache.TryGet(room, member, out _));
        }
        finally
        {
            await ComposeAsync(dir, "start");
        }

        await WaitForAsync(() => cache.IsUsable, TimeSpan.FromSeconds(12));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(memberClient, room)).StatusCode);
    }

    [Fact]
    public async Task RevocationRightAfterReconnect_IsNotMissed()
    {
        var dir = ComposeDir();
        await WaitUsableAsync();
        var cache = _factory.Services.GetRequiredService<AccessCache>();
        var admin = $"it-ac-{Guid.NewGuid():N}";
        await SeedAsync(admin, "admin");
        var adminClient = Client(admin, "admin");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await ComposeAsync(dir, "restart");
            await WaitForAsync(() => cache.IsUsable, TimeSpan.FromSeconds(15));

            var room = Guid.NewGuid();
            var member = $"it-ac-{Guid.NewGuid():N}";
            await SeedAsync(member, "member", room);
            var memberClient = Client(member, "member");

            Assert.Equal(HttpStatusCode.Accepted, (await PollUntilAsync(memberClient, room, HttpStatusCode.Accepted, TimeSpan.FromSeconds(10))));
            Assert.True(cache.TryGet(room, member, out _), $"cycle {cycle}: allow not cached");

            var removed = await adminClient.DeleteAsync($"/admin/rooms/{room}/members/{member}", Ct);
            Assert.True(removed.IsSuccessStatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, await PollUntilAsync(memberClient, room, HttpStatusCode.Forbidden, TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task BatchedQuery_RunsAgainstRealPostgres()
    {
        var room = Guid.NewGuid();
        var member = $"it-ac-{Guid.NewGuid():N}";
        var outsider = $"it-ac-{Guid.NewGuid():N}";
        var unknown = $"it-ac-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        await SeedAsync(outsider, "auditor");

        var store = _factory.Services.GetRequiredService<IAccessStore>();
        var result = await store.GetRolesAndMembershipsAsync(
            [(room, member), (room, outsider), (room, unknown)], Ct);

        Assert.Equal(("member", true), result[(room, member)]);
        Assert.Equal(("auditor", false), result[(room, outsider)]);
        Assert.Equal((null, false), result[(room, unknown)]);
    }
}
