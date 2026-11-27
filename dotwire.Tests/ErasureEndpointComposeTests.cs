using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Crypto;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class ErasureEndpointComposeTests : IAsyncDisposable
{
    private readonly string _ownerConnectionString;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<long> _placed = [];

    public ErasureEndpointComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        var app = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", app);
            b.UseSetting("ConnectionStrings:PostgresRead", app);
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
        });
        _ = _factory.Services;
        SeedAsync(SharedActor, "admin").GetAwaiter().GetResult();
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

    public async ValueTask DisposeAsync()
    {
        if (_placed.Count > 0)
        {
            await using var conn = new NpgsqlConnection(_ownerConnectionString);
            await conn.OpenAsync(CancellationToken.None);
            await using (var release = new NpgsqlCommand(
                "INSERT INTO legal_hold_releases (hold_id, released_by) " +
                "SELECT h.id, 'test-cleanup' FROM unnest($1::bigint[]) AS h(id) " +
                "WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)", conn))
            {
                release.Parameters.AddWithValue(_placed.ToArray());
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using var sync = new NpgsqlCommand("SELECT sync_retention_schedule()", conn);
            await sync.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await _factory.DisposeAsync();
    }

    private HttpClient AdminClient(string? actor = null)
    {
        actor ??= SharedActor;
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(actor, role: "admin"));
        return client;
    }

    private const string SharedActor = "it-era-actor";

    private static string NewUser() => $"it-era-{Guid.NewGuid():N}";

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

    private async Task<long> OwnerCountAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task SeedOldMessageAsync(Guid room, string sender, long seq)
    {
        var cipher = _factory.Services.GetRequiredService<MessageCipher>();
        var content = cipher.Encrypt(Encoding.UTF8.GetBytes("seeded"), room, sender);
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO messages (room_id, time, seq, sender_id, key_id, content) VALUES ($1, now() - interval '30 days', $2, $3, 'it', $4)", conn);
        cmd.Parameters.AddWithValue(room);
        cmd.Parameters.AddWithValue(seq);
        cmd.Parameters.AddWithValue(sender);
        cmd.Parameters.AddWithValue(content);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
    }

    [Fact]
    public async Task Erase_Returns202_RevokesRoleAndMemberships()
    {
        var user = NewUser();
        var room = Guid.NewGuid();
        await SeedAsync(user, "member", room);

        var response = await AdminClient().PostAsync($"/admin/users/{user}/erase", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<ErasureStatus>(Ct);
        Assert.Equal(user, status!.UserId);
        Assert.Equal("anonymize", status.Mode);
        Assert.Equal("in_progress", status.Status);
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM user_roles WHERE user_id = $1", user));
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM room_members WHERE user_id = $1", user));
        Assert.Equal(1, await OwnerCountAsync("SELECT count(*) FROM erasure_revocations WHERE erasure_id = $1", status.Id));

        long value = -1;
        for (var i = 0; i < 40 && value != 1; i++)
        {
            value = await OwnerCountAsync(
                "SELECT coalesce(max(value), -1) FROM audit_log WHERE event_type = 'user.erasure.requested' AND subject_id = $1", user);
            if (value != 1)
                await Task.Delay(250, Ct);
        }
        Assert.Equal(1, value);
    }

    [Fact]
    public async Task Erase_Twice_SameMode_SameId()
    {
        var user = NewUser();
        await SeedAsync(user, "member", Guid.NewGuid());
        var client = AdminClient();

        var first = await (await client.PostAsJsonAsync($"/admin/users/{user}/erase", new { mode = "delete" }, Ct)).Content.ReadFromJsonAsync<ErasureStatus>(Ct);
        var second = await client.PostAsJsonAsync($"/admin/users/{user}/erase", new { mode = "delete" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(first!.Id, (await second.Content.ReadFromJsonAsync<ErasureStatus>(Ct))!.Id);
        Assert.Equal(1, await OwnerCountAsync("SELECT count(*) FROM erasures WHERE user_id = $1", user));
    }

    [Fact]
    public async Task Erase_Twice_DifferentMode_Is409ModeConflict()
    {
        var user = NewUser();
        await SeedAsync(user, "member");
        var client = AdminClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/admin/users/{user}/erase", new { mode = "delete" }, Ct)).StatusCode);

        var second = await client.PostAsJsonAsync($"/admin/users/{user}/erase", new { mode = "anonymize" }, Ct);

        await AssertErrorAsync(second, HttpStatusCode.Conflict, "erasure_mode_conflict");
    }

    [Fact]
    public async Task Erase_Self_Is409()
    {
        var actor = NewUser();
        await SeedAsync(actor, "admin");

        var response = await AdminClient(actor).PostAsync($"/admin/users/{actor}/erase", null, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "self_erasure");
    }

    [Fact]
    public async Task Erase_LastAdmin_Is409()
    {
        var target = NewUser();
        var actor = NewUser();
        await SeedAsync(target, "admin");
        var store = new FakeAccessStore();
        store.Roles[actor] = "admin";
        var faked = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAccessStore>(store)));
        var client = faked.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(actor, role: "admin"));
        List<string> others = [];
        await using (var conn = new NpgsqlConnection(_ownerConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var del = new NpgsqlCommand("DELETE FROM user_roles WHERE role = 'admin' AND user_id <> $1 RETURNING user_id", conn);
            del.Parameters.AddWithValue(target);
            await using var reader = await del.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                others.Add(reader.GetString(0));
        }

        try
        {
            var response = await client.PostAsync($"/admin/users/{target}/erase", null, Ct);

            await AssertErrorAsync(response, HttpStatusCode.Conflict, "last_admin");
        }
        finally
        {
            await using var conn = new NpgsqlConnection(_ownerConnectionString);
            await conn.OpenAsync(CancellationToken.None);
            await using var restore = new NpgsqlCommand(
                "INSERT INTO user_roles (user_id, role) SELECT u, 'admin' FROM unnest($1::text[]) AS t(u) ON CONFLICT DO NOTHING", conn);
            restore.Parameters.AddWithValue(others.ToArray());
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Erase_InvalidMode_Is400()
    {
        var user = NewUser();

        var response = await AdminClient().PostAsJsonAsync($"/admin/users/{user}/erase", new { mode = "shred" }, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task RoleSet_DuringOpenErasure_Is409ErasureOpen()
    {
        var user = NewUser();
        await SeedAsync(user, "member");
        var client = AdminClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/admin/users/{user}/erase", null, Ct)).StatusCode);

        var response = await client.PutAsJsonAsync($"/admin/users/{user}/role", new { role = "member" }, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "erasure_open");
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM user_roles WHERE user_id = $1", user));
    }

    [Fact]
    public async Task MemberAdd_DuringOpenErasure_Is409ErasureOpen()
    {
        var user = NewUser();
        var other = NewUser();
        var room = Guid.NewGuid();
        await SeedAsync(user, "member");
        var client = AdminClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/admin/users/{user}/erase", null, Ct)).StatusCode);

        var plain = await client.PostAsJsonAsync($"/admin/rooms/{room}/members", new { userIds = new[] { other, user } }, Ct);
        var ensure = await client.PostAsJsonAsync($"/admin/rooms/{room}/members", new { userIds = new[] { user }, ensureMemberRole = true }, Ct);

        await AssertErrorAsync(plain, HttpStatusCode.Conflict, "erasure_open");
        await AssertErrorAsync(ensure, HttpStatusCode.Conflict, "erasure_open");
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM room_members WHERE room_id = $1", room));
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM user_roles WHERE user_id = $1", user));
    }

    [Fact]
    public async Task Inject_AsSenderWithOpenErasure_Is409ErasureOpen()
    {
        var user = NewUser();
        await SeedAsync(user, "member");
        var client = AdminClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/admin/users/{user}/erase", null, Ct)).StatusCode);

        var response = await client.PostAsJsonAsync($"/admin/rooms/{Guid.NewGuid()}/messages", new { content = "hi", senderId = user }, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "erasure_open");
    }

    [Fact]
    public async Task Status_HeldRoom_IsWaitingWithLegalHold()
    {
        var user = NewUser();
        var room = Guid.NewGuid();
        await SeedAsync(user, "member", room);
        await SeedOldMessageAsync(room, user, 1);
        var client = AdminClient();
        var hold = await client.PostAsJsonAsync("/admin/holds", new { roomId = room, matterRef = "m-" + Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
        _placed.Add((await hold.Content.ReadFromJsonAsync<LegalHold>(Ct))!.Id);

        var erase = await client.PostAsync($"/admin/users/{user}/erase", null, Ct);

        var status = await erase.Content.ReadFromJsonAsync<ErasureStatus>(Ct);
        Assert.Equal("waiting", status!.Status);
        Assert.Equal(1, status.Remaining);
        Assert.Equal(["legal_hold"], status.BlockedBy);
        var fetched = await client.GetFromJsonAsync<ErasureStatus>($"/admin/erasures/{status.Id}", Ct);
        Assert.Equal("waiting", fetched!.Status);
        var list = await client.GetFromJsonAsync<ErasureStatus[]>($"/admin/users/{user}/erasures", Ct);
        Assert.Single(list!);
    }

    [Fact]
    public async Task Status_NoMessages_IsInProgress()
    {
        var user = NewUser();
        await SeedAsync(user, "member");
        var erase = await AdminClient().PostAsync($"/admin/users/{user}/erase", null, Ct);
        var created = await erase.Content.ReadFromJsonAsync<ErasureStatus>(Ct);

        var status = await AdminClient().GetFromJsonAsync<ErasureStatus>($"/admin/erasures/{created!.Id}", Ct);

        Assert.Equal("in_progress", status!.Status);
        Assert.Equal(0, status.Remaining);
        Assert.Empty(status.BlockedBy);
        Assert.Null(status.CompletedAt);
    }

    [Fact]
    public async Task Status_Unknown_Is404()
    {
        var response = await AdminClient().GetAsync("/admin/erasures/9223372036854775807", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
