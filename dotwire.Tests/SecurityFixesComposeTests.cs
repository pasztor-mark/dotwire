using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class SecurityFixesComposeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;

    public SecurityFixesComposeTests(WebApplicationFactory<Program> factory)
    {
        SkipUnlessReachable("localhost", 5432, "Postgres");
        SkipUnlessReachable("localhost", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}";

        _baseFactory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", _appConnectionString);
            b.UseSetting("ConnectionStrings:PostgresRead", _appConnectionString);
            b.UseSetting("Postgres:AppRolePassword", appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://localhost:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            b.UseSetting("Dotwire:RateLimits:Enabled", "false");
        });
        _factory = _baseFactory;
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

    private async Task<long> CountAuditAsync(string eventType, string subjectId, int expectedAtLeast)
    {
        long count = 0;
        for (var i = 0; i < 40; i++)
        {
            await using var conn = new NpgsqlConnection(_ownerConnectionString);
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE event_type = $1 AND subject_id = $2", conn);
            cmd.Parameters.AddWithValue(eventType);
            cmd.Parameters.AddWithValue(subjectId);
            count = (long)(await cmd.ExecuteScalarAsync(Ct))!;
            if (count >= expectedAtLeast)
                return count;
            await Task.Delay(250, Ct);
        }
        return count;
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string token)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HubConnection Hub(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hub/rooms"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, ct) =>
                {
                    var ws = _factory.Server.CreateWebSocketClient();
                    ws.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                    return await ws.ConnectAsync(context.Uri, ct);
                };
            })
            .Build();

    [Fact]
    public async Task RoleChange_RevokesLiveHubSubscriptions()
    {
        var room = Guid.NewGuid();
        var member = $"it-sec-{Guid.NewGuid():N}";
        var admin = $"it-sec-admin-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        await SeedAsync(admin, "admin");

        await using var connection = Hub(TestTokens.Mint(member));
        var revoked = new TaskCompletionSource<MembershipRevoked>();
        connection.On<MembershipRevoked>("MembershipRevoked", r => revoked.TrySetResult(r));
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("Subscribe", room, Ct);
        await Task.Delay(200, Ct);

        var response = await Client(_factory, TestTokens.Mint(admin, role: "admin"))
            .PutAsJsonAsync($"/admin/users/{member}/role", new { role = "auditor" }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(room, evt.RoomId);
    }

    [Fact]
    public async Task RoleDelete_AuditsEveryDroppedMembership()
    {
        var rooms = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var member = $"it-sec-{Guid.NewGuid():N}";
        var admin = $"it-sec-admin-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", rooms);
        await SeedAsync(admin, "admin");

        var response = await Client(_factory, TestTokens.Mint(admin, role: "admin")).DeleteAsync($"/admin/users/{member}/role", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, await CountAuditAsync("member.removed", member, 2));
        Assert.Equal(1, await CountAuditAsync("role.deleted", member, 1));
    }

    [Fact]
    public async Task AddMembers_EnsureMemberRole_AuditsTheRoleGrant()
    {
        var admin = $"it-sec-admin-{Guid.NewGuid():N}";
        var fresh = $"it-sec-fresh-{Guid.NewGuid():N}";
        await SeedAsync(admin, "admin");

        var response = await Client(_factory, TestTokens.Mint(admin, role: "admin"))
            .PostAsJsonAsync($"/admin/rooms/{Guid.NewGuid()}/members", new AddMembersRequest([fresh], true), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountAuditAsync("role.set.member", fresh, 1));
    }

    [Fact]
    public async Task Redact_FutureSeq_Is404AndWritesNoTombstone()
    {
        var room = Guid.NewGuid();
        var admin = $"it-sec-admin-{Guid.NewGuid():N}";
        await SeedAsync(admin, "admin");
        var js = _factory.Services.GetRequiredService<INatsJSContext>();
        var stream = await js.GetStreamAsync(_factory.Services.GetRequiredService<IOptions<NatsOptions>>().Value.RoomsStream, cancellationToken: Ct);
        var futureSeq = stream.Info.State.LastSeq + 1_000_000;

        var response = await Client(_factory, TestTokens.Mint(admin, role: "admin"))
            .DeleteAsync($"/admin/rooms/{room}/messages/{futureSeq}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM message_redactions WHERE room_id = $1", conn);
        cmd.Parameters.AddWithValue(room);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task Injection_AuditFailure_LeavesNothingInTheStream()
    {
        var room = Guid.NewGuid();
        var admin = $"it-sec-admin-{Guid.NewGuid():N}";
        await SeedAsync(admin, "admin");
        var app = _baseFactory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<AuditPublisher>(new ThrowingAuditPublisher())));
        var js = app.Services.GetRequiredService<INatsJSContext>();
        var options = app.Services.GetRequiredService<IOptions<NatsOptions>>().Value;

        var response = await Client(app, TestTokens.Mint(admin, role: "admin"))
            .PostAsJsonAsync($"/admin/rooms/{room}/messages", new { content = "unaudited" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var stream = await js.GetStreamAsync(options.RoomsStream, cancellationToken: Ct);
        var exception = await Record.ExceptionAsync(() =>
            stream.GetAsync(new StreamMsgGetRequest { LastBySubj = $"{options.RoomSubjectPrefix}{room}" }, Ct).AsTask());
        Assert.IsType<NatsJSApiException>(exception);
    }

    [Fact]
    public async Task Send_RetryWithSameIdempotencyKey_ReturnsTheOriginalSeq()
    {
        var room = Guid.NewGuid();
        var member = $"it-sec-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        var client = Client(_factory, TestTokens.Mint(member));
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"k-{Guid.NewGuid():N}");

        var first = await (await client.PostAsJsonAsync($"/rooms/{room}/messages", new { content = "once" }, Ct))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Ct);
        var second = await (await client.PostAsJsonAsync($"/rooms/{room}/messages", new { content = "once" }, Ct))
            .Content.ReadFromJsonAsync<SendMessageResponse>(Ct);

        Assert.Equal(first!.Seq, second!.Seq);
    }

    [Fact]
    public async Task AuditCheckpointTrigger_RejectsFutureAndMismatchedCheckpoints()
    {
        await using var conn = new NpgsqlConnection(_appConnectionString);
        await conn.OpenAsync(Ct);

        async Task<PostgresException?> TryInsert(string day, long lastId, byte[] hash)
        {
            await using var tx = await conn.BeginTransactionAsync(Ct);
            await using var cmd = new NpgsqlCommand($"INSERT INTO audit_checkpoints (day, last_id, hash) VALUES ('{day}', $1, $2)", conn, tx);
            cmd.Parameters.AddWithValue(lastId);
            cmd.Parameters.AddWithValue(hash);
            try
            {
                await cmd.ExecuteNonQueryAsync(Ct);
                return null;
            }
            catch (PostgresException ex)
            {
                return ex;
            }
            finally
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
        }

        Assert.NotNull(await TryInsert("9999-12-31", long.MaxValue, new byte[32]));
        Assert.NotNull(await TryInsert("2000-01-01", long.MaxValue, new byte[32]));
    }

    [Fact]
    public async Task AuditLog_DuplicatePrevHashIsRejected()
    {
        await using var conn = new NpgsqlConnection(_appConnectionString);
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var prev = RandomNumberGenerator.GetBytes(32);

        async Task Insert(long streamSeq)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO audit_log (event_type, prev_hash, hash, event_time, stream_seq) VALUES ('it.fork', $1, $2, now(), $3)", conn, tx);
            cmd.Parameters.AddWithValue(prev);
            cmd.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
            cmd.Parameters.AddWithValue(streamSeq);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        await Insert(-Random.Shared.NextInt64(1, long.MaxValue));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Insert(-Random.Shared.NextInt64(1, long.MaxValue)));
        await tx.RollbackAsync(CancellationToken.None);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Migrations_RecordChecksums()
    {
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT filename, checksum FROM schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var stored = new Dictionary<string, string?>();
        while (await reader.ReadAsync(Ct))
            stored[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);

        foreach (var (name, sql) in MigrationRunner.LoadScripts())
            Assert.Equal(MigrationRunner.Checksum(sql), stored[name]);
    }
}
