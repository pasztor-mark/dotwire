using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Full-pipeline tests for <see cref="DotwireHostClient"/> against a real dotwire
/// (compose-provided Postgres + NATS: docker compose up -d postgres nats), exercising the
/// SDK's actual wire routing/identity minting - not just error mapping (see
/// <see cref="DotwireHostClientUnitTests"/> for the no-server coverage).
/// </summary>
[Collection("Compose")]
public class DotwireHostClientComposeTests
{
    private const string PgConnectionString =
        "Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password=";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _pgPassword;

    public DotwireHostClientComposeTests()
    {
        SkipUnlessReachable("localhost", 5432, "Postgres");
        SkipUnlessReachable("localhost", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        _pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresMigrator",
                $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password={_pgPassword}");
            b.UseSetting("ConnectionStrings:PostgresWrite",
                $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
            b.UseSetting("ConnectionStrings:PostgresRead",
                $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://localhost:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });
        _ = _factory.Services;
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

    /// <summary>
    /// A fresh <see cref="DotwireHostClient"/> wired against the in-process factory, minting
    /// tokens under a throwaway key that dotwire is configured to trust (Auth:Keys:test-key-1).
    /// </summary>
    private DotwireHostClient MakeClient(string? auditorUserId = null)
    {
        var http = _factory.CreateClient();
        var options = new DotwireHostOptions
        {
            BaseUrl = http.BaseAddress!,
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            KeyId = TestKeys.Kid,
            PrivateKeyPem = TestKeys.PrivatePem,
            AuditorUserId = auditorUserId ?? DotwireHostOptions.DefaultAuditorUserId,
            RoleCacheTtl = TimeSpan.FromSeconds(30),
        };
        return new DotwireHostClient(http, new DotwireTokenSigner(options), options);
    }

    private async Task SeedRoleAsync(string userId, string role)
    {
        await using var conn = new NpgsqlConnection(PgConnectionString + _pgPassword);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role) VALUES ($1, $2) ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role", conn);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(role);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedMemberAsync(string userId, Guid roomId)
    {
        await SeedRoleAsync(userId, "member");
        await using var conn = new NpgsqlConnection(PgConnectionString + _pgPassword);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    // ---- roles / membership -------------------------------------------------------

    [Fact]
    public async Task SetGetDeleteUserRole_RoundTrips()
    {
        var client = MakeClient();
        var userId = $"role-rt-{Guid.NewGuid():N}";

        var set = await client.SetUserRoleAsync(userId, "member", TestContext.Current.CancellationToken);
        Assert.Equal("member", set.Role);

        var got = await client.GetUserRoleAsync(userId, TestContext.Current.CancellationToken);
        Assert.NotNull(got);
        Assert.Equal("member", got!.Role);

        await client.DeleteUserRoleAsync(userId, TestContext.Current.CancellationToken);
        var afterDelete = await client.GetUserRoleAsync(userId, TestContext.Current.CancellationToken);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task GetUserRoleAsync_UnknownUser_ReturnsNull()
    {
        var client = MakeClient();
        var result = await client.GetUserRoleAsync($"nobody-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task AddGetRemoveRoomMembers_RoundTrips()
    {
        var client = MakeClient();
        var roomId = Guid.NewGuid();
        var userId = $"member-{Guid.NewGuid():N}";

        var added = await client.AddRoomMembersAsync(roomId, [userId], ensureMemberRole: true, TestContext.Current.CancellationToken);
        Assert.Contains(userId, added);

        var members = await client.GetRoomMembersAsync(roomId, TestContext.Current.CancellationToken);
        Assert.Contains(userId, members);

        var rooms = await client.GetUserRoomsAsync(userId, TestContext.Current.CancellationToken);
        Assert.Contains(roomId, rooms);

        await client.RemoveRoomMemberAsync(roomId, userId, TestContext.Current.CancellationToken);
        var afterRemove = await client.GetRoomMembersAsync(roomId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(userId, afterRemove);
    }

    /// <summary>
    /// Messages land in Postgres via the batch-insert consumer (up to Nats:BatchLingerMs,
    /// 1s by default), not synchronously with the send ack - poll rather than sleep a fixed
    /// amount so this isn't flaky under load.
    /// </summary>
    private static async Task<RoomHistory> WaitForMessageAsync(DotwireHostClient client, Guid roomId, string asUserId, ulong seq, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            var history = await client.GetMessagesAsync(roomId, asUserId, ct: ct);
            if (history.Messages.Any(m => m.Seq == seq) || DateTimeOffset.UtcNow > deadline)
                return history;
            await Task.Delay(150, ct);
        }
    }

    // ---- send / history / redact -------------------------------------------------------

    [Fact]
    public async Task SendAsUserAsync_MemberAlreadyRight_SendsOnFirstAttempt()
    {
        var client = MakeClient();
        var roomId = Guid.NewGuid();
        var userId = $"sender-{Guid.NewGuid():N}";
        await SeedMemberAsync(userId, roomId);

        var ack = await client.SendAsUserAsync(roomId, userId, "hello world", TestContext.Current.CancellationToken);
        Assert.True(ack.Seq > 0);
    }

    [Fact]
    public async Task SendAsUserAsync_ResolvesAdminRole_ThenSucceeds()
    {
        var client = MakeClient();
        var roomId = Guid.NewGuid();
        var userId = $"admin-sender-{Guid.NewGuid():N}";
        // Actual role is admin (not member) - the initial `member` attempt 403s via
        // RoleCrossCheckFilter, so the SDK must resolve and retry.
        await SeedRoleAsync(userId, "admin");
        await using (var conn = new NpgsqlConnection(PgConnectionString + _pgPassword))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.AddWithValue(roomId);
            cmd.Parameters.AddWithValue(userId);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var ack = await client.SendAsUserAsync(roomId, userId, "hello as admin", TestContext.Current.CancellationToken);
        Assert.True(ack.Seq > 0);
    }

    [Fact]
    public async Task SendSystemMessageAsync_And_GetMessagesAsync_And_Redact()
    {
        var client = MakeClient();
        var roomId = Guid.NewGuid();
        var userId = $"reader-{Guid.NewGuid():N}";
        await SeedMemberAsync(userId, roomId);

        var ack = await client.SendSystemMessageAsync(roomId, "system announcement", ct: TestContext.Current.CancellationToken);
        Assert.True(ack.Seq > 0);

        var history = await WaitForMessageAsync(client, roomId, userId, ack.Seq, TestContext.Current.CancellationToken);
        Assert.Contains(history.Messages, m => m.Seq == ack.Seq && m.Content == "system announcement");

        var redacted = await client.RedactMessageAsync(roomId, ack.Seq, TestContext.Current.CancellationToken);
        Assert.True(redacted.RemovedFromHistory);

        var historyAfter = await client.GetMessagesAsync(roomId, userId, ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(historyAfter.Messages, m => m.Seq == ack.Seq);
    }

    // ---- audit -------------------------------------------------------

    [Fact]
    public async Task AuditRoundTrip_ReadVerifyCheckpoints()
    {
        var auditorId = $"auditor-{Guid.NewGuid():N}";
        await SeedRoleAsync(auditorId, "auditor");
        var client = MakeClient(auditorId);

        // Generate at least one audit event to read back.
        var adminClient = MakeClient();
        await adminClient.SetUserRoleAsync($"audit-target-{Guid.NewGuid():N}", "member", TestContext.Current.CancellationToken);

        var page = await client.ReadAuditLogAsync(0, 10, TestContext.Current.CancellationToken);
        Assert.NotEmpty(page.Events);

        var verification = await client.VerifyAuditLogAsync(full: true, ct: TestContext.Current.CancellationToken);
        Assert.True(verification.Ok);

        // Checkpoints may legitimately be empty in a fresh database (they're written by a
        // daily job) - just confirm the call routes and parses.
        var checkpoints = await client.GetAuditCheckpointsAsync(5, TestContext.Current.CancellationToken);
        Assert.NotNull(checkpoints);
    }

    // ---- retention -------------------------------------------------------

    [Fact]
    public async Task RetentionRoundTrip()
    {
        var client = MakeClient();
        var set = await client.SetMessageRetentionDaysAsync(30, TestContext.Current.CancellationToken);
        Assert.Equal(30, set);

        var got = await client.GetMessageRetentionDaysAsync(TestContext.Current.CancellationToken);
        Assert.Equal(30, got);
    }

    // ---- DSAR export -------------------------------------------------------

    [Fact]
    public async Task ExportUserAsync_IncludesSentMessage()
    {
        var client = MakeClient();
        var roomId = Guid.NewGuid();
        var userId = $"export-{Guid.NewGuid():N}";
        await SeedMemberAsync(userId, roomId);

        var ack = await client.SendAsUserAsync(roomId, userId, "exportable content", TestContext.Current.CancellationToken);
        await WaitForMessageAsync(client, roomId, userId, ack.Seq, TestContext.Current.CancellationToken);

        var export = await client.ExportUserAsync(userId, TestContext.Current.CancellationToken);
        Assert.Equal(userId, export.UserId);
        Assert.Contains(roomId, export.RoomIds);
        Assert.Contains(export.Messages, m => m.Seq == ack.Seq && m.Content == "exportable content");

        await using var stream = new MemoryStream();
        await client.ExportUserAsync(userId, stream, TestContext.Current.CancellationToken);
        Assert.True(stream.Length > 0);
    }

    // ---- SSE against the real server -------------------------------------------------------

    [Fact]
    public async Task StreamEventsAsync_ReceivesLiveMessage()
    {
        var senderClient = MakeClient();
        var readerClient = MakeClient();
        var roomId = Guid.NewGuid();
        var senderId = $"stream-sender-{Guid.NewGuid():N}";
        var readerId = $"stream-reader-{Guid.NewGuid():N}";
        await SeedMemberAsync(senderId, roomId);
        await SeedMemberAsync(readerId, roomId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var evt in readerClient.StreamEventsAsync(roomId, readerId, ct: cts.Token))
            {
                if (evt is MessageEvent { Replayed: false } msg && msg.Content == "live via sse")
                    return msg;
            }
            return null;
        }, cts.Token);

        // Give the SSE connection a moment to register before publishing.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await senderClient.SendAsUserAsync(roomId, senderId, "live via sse", TestContext.Current.CancellationToken);

        var received = await receiveTask;
        Assert.NotNull(received);
        Assert.Equal("live via sse", received!.Content);
        await cts.CancelAsync();
    }
}
