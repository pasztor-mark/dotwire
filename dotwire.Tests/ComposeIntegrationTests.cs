using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Full-pipeline tests against compose-provided Postgres + NATS (docker compose up -d
/// postgres nats). Cleanly skipped when either service isn't reachable. The factory
/// hosts the real app in-process: migrations on, provisioning on, real auth.
/// </summary>
[Collection("Compose")]
public class ComposeIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string PgConnectionString =
        "Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password=";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _pgPassword;

    public ComposeIntegrationTests(WebApplicationFactory<Program> factory)
    {
        SkipUnlessReachable("localhost", 5432, "Postgres");
        SkipUnlessReachable("localhost", 4222, "NATS");

        // The app loads .env itself (DotNetEnv.TraversePath); tests reuse the same secrets.
        DotNetEnv.Env.TraversePath().Load();
        _pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");

        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _factory = factory.WithWebHostBuilder(b =>
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

        // Force the host to start now (migrations run as part of Program's top-level
        // startup, before app.Run()). Tests below seed via a raw psql connection ahead
        // of any HTTP call, so without this the schema/dotwire_app role wouldn't exist
        // yet the first time a test class instance runs against a fresh database
        // (test bug, not a product bug: WithWebHostBuilder's host is otherwise lazy).
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

    private async Task<NpgsqlConnection> OpenOwnerConnectionAsync()
    {
        var conn = new NpgsqlConnection(PgConnectionString + _pgPassword);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private async Task SeedUserAsync(string sub, string role, Guid? memberOf = null)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO user_roles (user_id, role) VALUES ($1, $2)
            ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role
            """, conn))
        {
            cmd.Parameters.AddWithValue(sub);
            cmd.Parameters.AddWithValue(role);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        if (memberOf is { } roomId)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.AddWithValue(roomId);
            cmd.Parameters.AddWithValue(sub);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task HappyPath_SendIsAckedAndLandsInPostgres()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);
        var client = Client(TestTokens.Mint(sub));

        var response = await client.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = "integration says hi" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(body.Seq > 0);

        // The ack is JetStream, not Postgres . history lags ~one batch interval, so POLL,
        // never assert immediately (pre-implementation.md trap #4).
        var found = false;
        for (var i = 0; i < 40 && !found; i++)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            await using var conn = await OpenOwnerConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM messages WHERE room_id = $1 AND seq = $2", conn);
            cmd.Parameters.AddWithValue(roomId);
            cmd.Parameters.AddWithValue((long)body.Seq);
            found = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))! == 1;
        }
        Assert.True(found, "message row never appeared in Postgres within 10s");

        // Stored content is ciphertext, sender is the token's sub.
        await using var verify = await OpenOwnerConnectionAsync();
        await using var check = new NpgsqlCommand(
            "SELECT sender_id, key_id, content FROM messages WHERE room_id = $1 AND seq = $2", verify);
        check.Parameters.AddWithValue(roomId);
        check.Parameters.AddWithValue((long)body.Seq);
        await using var reader = await check.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sub, reader.GetString(0));
        Assert.Equal("it", reader.GetString(1));
        var stored = (byte[])reader[2];
        // Assert.DoesNotContain(item, [collection]) is ambiguous in xunit v3 (HashSet vs
        // SortedSet overloads both apply to the collection-expression literal) . same
        // intent (stored bytes != plaintext bytes) via SequenceEqual instead.
        Assert.False(stored.SequenceEqual("integration says hi"u8.ToArray())); // not plaintext
    }

    [Fact]
    public async Task SeqIsMonotonicPerRoom()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);
        var client = Client(TestTokens.Mint(sub));

        var seqs = new List<ulong>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/rooms/{roomId}/messages", new { content = $"msg {i}" },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var ack = await response.Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken);
            seqs.Add(ack!.Seq);
        }

        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs);
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);
    }

    [Fact]
    public async Task RoleMismatch_Is403()
    {
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member");
        var client = Client(TestTokens.Mint(sub, role: "admin")); // token claims more than the table grants

        var response = await client.PostAsJsonAsync(
            $"/rooms/{Guid.NewGuid()}/messages", new { content = "hi" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownUser_Is403()
    {
        var client = Client(TestTokens.Mint($"never-seeded-{Guid.NewGuid():N}"));

        var response = await client.PostAsJsonAsync(
            $"/rooms/{Guid.NewGuid()}/messages", new { content = "hi" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonMember_Is403()
    {
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member"); // role ok, but no room membership
        var client = Client(TestTokens.Mint(sub));

        var response = await client.PostAsJsonAsync(
            $"/rooms/{Guid.NewGuid()}/messages", new { content = "hi" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonUuidRoom_Is404()
    {
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member");
        var client = Client(TestTokens.Mint(sub));

        var response = await client.PostAsJsonAsync(
            "/rooms/not-a-room/messages", new { content = "hi" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EmptyAndOversizedContent_Are400()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);
        var client = Client(TestTokens.Mint(sub));

        var empty = await client.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = "" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var oversized = await client.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = new string('x', 70_000) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task AuditLogUpdate_AsAppRole_IsDenied()
    {
        // Owner bypasses grants . testing as dotwire proves nothing (trap #2).
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        await using var conn = new NpgsqlConnection(
            $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = new NpgsqlCommand("UPDATE audit_log SET event_type = 'x'", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    private HubConnection BuildHubConnection(string token)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hub/rooms"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, ct) =>
                {
                    var wsClient = _factory.Server.CreateWebSocketClient();
                    return await wsClient.ConnectAsync(context.Uri, ct);
                };
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    [Fact]
    public async Task LiveMessageFanout_IsReceivedAndDecrypted()
    {
        var roomId = Guid.NewGuid();
        var subA = $"it-user-a-{Guid.NewGuid():N}";
        var subB = $"it-user-b-{Guid.NewGuid():N}";
        await SeedUserAsync(subA, "member", roomId);
        await SeedUserAsync(subB, "member", roomId);

        var tokenA = TestTokens.Mint(subA);
        await using var connectionA = BuildHubConnection(tokenA);
        await connectionA.StartAsync(TestContext.Current.CancellationToken);

        var messageTcs = new TaskCompletionSource<RoomMessageDelivery>();
        connectionA.On<RoomMessageDelivery>("ReceiveMessage", msg => messageTcs.TrySetResult(msg));

        await connectionA.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        var clientB = Client(TestTokens.Mint(subB));
        var response = await clientB.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = "live hello over nats" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);

        var received = await messageTcs.Task.WaitAsync(linkedCts.Token);
        Assert.NotNull(received);
        Assert.Equal(roomId, received.RoomId);
        Assert.Equal(subB, received.SenderId);
        Assert.Equal("live hello over nats", received.Content);

        await connectionA.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TypingIndicator_IsReceivedByRoomMembers()
    {
        var roomId = Guid.NewGuid();
        var subA = $"it-user-a-{Guid.NewGuid():N}";
        var subB = $"it-user-b-{Guid.NewGuid():N}";
        await SeedUserAsync(subA, "member", roomId);
        await SeedUserAsync(subB, "member", roomId);

        var tokenA = TestTokens.Mint(subA);
        var tokenB = TestTokens.Mint(subB);
        await using var connectionA = BuildHubConnection(tokenA);
        await using var connectionB = BuildHubConnection(tokenB);

        await connectionA.StartAsync(TestContext.Current.CancellationToken);
        await connectionB.StartAsync(TestContext.Current.CancellationToken);

        var typingTcs = new TaskCompletionSource<TypingNotification>();
        connectionA.On<TypingNotification>("UserTyping", t => typingTcs.TrySetResult(t));

        await connectionA.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
        await connectionB.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        await connectionB.InvokeAsync("Typing", roomId, true, TestContext.Current.CancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);

        var received = await typingTcs.Task.WaitAsync(linkedCts.Token);
        Assert.NotNull(received);
        Assert.Equal(roomId, received.RoomId);
        Assert.Equal(subB, received.UserId);
        Assert.True(received.IsTyping);

        await connectionA.StopAsync(TestContext.Current.CancellationToken);
        await connectionB.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PresenceCoalescing_DeliversDeltas()
    {
        var roomId = Guid.NewGuid();
        var subA = $"it-user-a-{Guid.NewGuid():N}";
        var subB = $"it-user-b-{Guid.NewGuid():N}";
        await SeedUserAsync(subA, "member", roomId);
        await SeedUserAsync(subB, "member", roomId);

        var tokenA = TestTokens.Mint(subA);
        var tokenB = TestTokens.Mint(subB);
        await using var connectionA = BuildHubConnection(tokenA);
        await using var connectionB = BuildHubConnection(tokenB);

        await connectionA.StartAsync(TestContext.Current.CancellationToken);
        await connectionB.StartAsync(TestContext.Current.CancellationToken);

        var presenceTcs = new TaskCompletionSource<PresenceDelta>();
        connectionA.On<PresenceDelta>("PresenceUpdated", p =>
        {
            if (p.Joined.Contains(subB))
                presenceTcs.TrySetResult(p);
        });

        await connectionA.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await connectionB.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);

        var delta = await presenceTcs.Task.WaitAsync(linkedCts.Token);
        Assert.NotNull(delta);
        Assert.Equal(roomId, delta.RoomId);
        Assert.Contains(subB, delta.Joined);

        await connectionA.StopAsync(TestContext.Current.CancellationToken);
        await connectionB.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SignalR_Subscribe_WhenNonMember_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member");

        var token = TestTokens.Mint(sub);
        await using var connection = BuildHubConnection(token);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken));
        Assert.Contains("Forbidden", ex.Message);

        await connection.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed record SendAck(ulong Seq, DateTimeOffset Time);
}
