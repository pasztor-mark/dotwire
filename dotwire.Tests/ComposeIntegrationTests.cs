using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
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
    public async Task Participants_ListsRoomMembersOnlyForAnotherMember()
    {
        var roomId = Guid.NewGuid();
        var otherRoomId = Guid.NewGuid();
        var requester = $"it-user-{Guid.NewGuid():N}";
        var peer = $"it-user-{Guid.NewGuid():N}";
        var outsider = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(requester, "member", roomId);
        await SeedUserAsync(peer, "member", roomId);
        await SeedUserAsync(outsider, "member", otherRoomId);

        using var memberClient = Client(TestTokens.Mint(requester));
        var response = await memberClient.GetAsync($"/rooms/{roomId}/participants", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var userIds = await response.Content.ReadFromJsonAsync<string[]>(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { peer, requester }.Order(StringComparer.Ordinal), userIds);

        using var outsiderClient = Client(TestTokens.Mint(outsider));
        var forbidden = await outsiderClient.GetAsync($"/rooms/{roomId}/participants", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var anonymousClient = _factory.CreateClient();
        var unauthorized = await anonymousClient.GetAsync($"/rooms/{roomId}/participants", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        await using (var conn = await OpenOwnerConnectionAsync())
        await using (var cmd = new NpgsqlCommand("DELETE FROM room_members WHERE room_id = $1 AND user_id = $2", conn))
        {
            cmd.Parameters.AddWithValue(roomId);
            cmd.Parameters.AddWithValue(requester);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var revoked = await memberClient.GetAsync($"/rooms/{roomId}/participants", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
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
        var emptyBody = await empty.Content.ReadFromJsonAsync<ApiErrorResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_content", emptyBody!.Error);

        var oversized = await client.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = new string('x', 70_000) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        var oversizedBody = await oversized.Content.ReadFromJsonAsync<ApiErrorResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_content", oversizedBody!.Error);
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
        var ack = await response.Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);

        var received = await messageTcs.Task.WaitAsync(linkedCts.Token);
        Assert.NotNull(received);
        Assert.Equal(roomId, received.RoomId);
        Assert.Equal(subB, received.SenderId);
        Assert.Equal("live hello over nats", received.Content);
        // Live delivery carries the same seq as the JetStream ack (spec §3.2, §10.2).
        Assert.Equal(ack!.Seq, received.Seq);

        await connectionA.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Typing_DoesNotGrowTheRoomsStream()
    {
        var roomId = Guid.NewGuid();
        var subA = $"it-user-a-{Guid.NewGuid():N}";
        await SeedUserAsync(subA, "member", roomId);

        var tokenA = TestTokens.Mint(subA);
        await using var connectionA = BuildHubConnection(tokenA);
        await connectionA.StartAsync(TestContext.Current.CancellationToken);
        await connectionA.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var js = new NatsJSContext(new NATS.Client.Core.NatsConnection(
            new NATS.Client.Core.NatsOpts { Url = "nats://localhost:4222" }));
        var before = (await js.GetStreamAsync("ROOMS", cancellationToken: TestContext.Current.CancellationToken))
            .Info.State.Messages;

        await connectionA.InvokeAsync("Typing", roomId, true, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var after = (await js.GetStreamAsync("ROOMS", cancellationToken: TestContext.Current.CancellationToken))
            .Info.State.Messages;

        // room.* only matches room.{roomId} (JetStream) - ephemeral subjects like
        // room.{roomId}.ephemeral.typing and room.{roomId}.live have an extra token and
        // stay off the stream (spec §3.1).
        Assert.Equal(before, after);

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

    [Fact]
    public async Task History_WhenNonMember_Is403()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member");

        var client = Client(TestTokens.Mint(sub));
        var response = await client.GetAsync($"/rooms/{roomId}/messages", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task History_ReturnsDecryptedMessagesAndSupportsGapFill()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);

        var client = Client(TestTokens.Mint(sub));

        var emptyResponse = await client.GetFromJsonAsync<RoomHistoryResponse>(
            $"/rooms/{roomId}/messages", TestContext.Current.CancellationToken);
        Assert.NotNull(emptyResponse);
        Assert.Empty(emptyResponse.Messages);
        Assert.False(emptyResponse.HasMore);

        for (var i = 1; i <= 3; i++)
        {
            var sendResponse = await client.PostAsJsonAsync(
                $"/rooms/{roomId}/messages",
                new SendMessageRequest($"history message {i}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, sendResponse.StatusCode);
        }

        RoomHistoryResponse? history = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            history = await client.GetFromJsonAsync<RoomHistoryResponse>(
                $"/rooms/{roomId}/messages", TestContext.Current.CancellationToken);
            if (history is not null && history.Messages.Length == 3)
                break;
        }

        Assert.NotNull(history);
        Assert.Equal(3, history.Messages.Length);
        Assert.Equal("history message 1", history.Messages[0].Content);
        Assert.Equal("history message 2", history.Messages[1].Content);
        Assert.Equal("history message 3", history.Messages[2].Content);

        var firstSeq = history.Messages[0].Seq;
        var gapFill = await client.GetFromJsonAsync<RoomHistoryResponse>(
            $"/rooms/{roomId}/messages?afterSeq={firstSeq}", TestContext.Current.CancellationToken);

        Assert.NotNull(gapFill);
        Assert.Equal(2, gapFill.Messages.Length);
        Assert.Equal("history message 2", gapFill.Messages[0].Content);
        Assert.Equal("history message 3", gapFill.Messages[1].Content);
    }

    private async Task<long> CountRowsAsync(Guid roomId, ulong seq)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM messages WHERE room_id = $1 AND seq = $2", conn);
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue((long)seq);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task WaitForRowAsync(Guid roomId, ulong seq)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await CountRowsAsync(roomId, seq) == 1)
                return;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.Fail("message row never appeared in Postgres within 10s");
    }

    [Fact]
    public async Task Redaction_ErasesBothStores_AuditsIdsOnly_AndRetractsLive()
    {
        var roomId = Guid.NewGuid();
        var member = $"it-user-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomId);
        await SeedUserAsync(admin, "admin");

        await using var connection = BuildHubConnection(TestTokens.Mint(member));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var retractedTcs = new TaskCompletionSource<MessageRetracted>();
        connection.On<MessageRetracted>("MessageRetracted", r => retractedTcs.TrySetResult(r));
        await connection.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var memberClient = Client(TestTokens.Mint(member));
        var send = await memberClient.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = "to be erased" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var ack = (await send.Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken))!;
        await WaitForRowAsync(roomId, ack.Seq);

        var js = _factory.Services.GetRequiredService<INatsJSContext>();
        var audit = await js.GetStreamAsync("AUDIT", cancellationToken: TestContext.Current.CancellationToken);
        var auditBefore = audit.Info.State.Messages;

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var redact = await adminClient.DeleteAsync(
            $"/admin/rooms/{roomId}/messages/{ack.Seq}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, redact.StatusCode);
        var body = (await redact.Content.ReadFromJsonAsync<RedactMessageResponse>(TestContext.Current.CancellationToken))!;
        Assert.True(body.RemovedFromHistory);
        Assert.True(body.RemovedFromStream);

        // Physically gone from Postgres and from history reads.
        Assert.Equal(0, await CountRowsAsync(roomId, ack.Seq));
        var history = (await memberClient.GetFromJsonAsync<RoomHistoryResponse>(
            $"/rooms/{roomId}/messages", TestContext.Current.CancellationToken))!;
        Assert.DoesNotContain(history.Messages, m => m.Seq == ack.Seq);

        // Exactly one audit event appended, ids only - the payload is checked by shape here
        // (the AuditEvent record has no content field at all), the count by stream state.
        await audit.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(auditBefore + 1, audit.Info.State.Messages);

        // The live subscriber was told to drop it.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);
        var retracted = await retractedTcs.Task.WaitAsync(linkedCts.Token);
        Assert.Equal(roomId, retracted.RoomId);
        Assert.Equal(ack.Seq, retracted.Seq);

        // Idempotent: a retry after success is still a 200, with nothing left to remove.
        var again = await adminClient.DeleteAsync(
            $"/admin/rooms/{roomId}/messages/{ack.Seq}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = (await again.Content.ReadFromJsonAsync<RedactMessageResponse>(TestContext.Current.CancellationToken))!;
        Assert.False(againBody.RemovedFromHistory);
        Assert.False(againBody.RemovedFromStream);

        await connection.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Redaction_BeforeBatchWriterLands_NeverReachesPostgres()
    {
        // History lags the JetStream ack by ~one batch interval (ARCHITECTURE.md, "Write
        // path"). A redaction that races ahead of the writer must still win: the tombstone
        // trigger in 0006_redaction.sql skips the late insert.
        var roomId = Guid.NewGuid();
        var member = $"it-user-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomId);
        await SeedUserAsync(admin, "admin");

        var memberClient = Client(TestTokens.Mint(member));
        var send = await memberClient.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = "racing the writer" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var ack = (await send.Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken))!;

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var redact = await adminClient.DeleteAsync(
            $"/admin/rooms/{roomId}/messages/{ack.Seq}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, redact.StatusCode);

        // Well past the writer's 1s linger: the row must never show up.
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.Equal(0, await CountRowsAsync(roomId, ack.Seq));
        }

        var history = (await memberClient.GetFromJsonAsync<RoomHistoryResponse>(
            $"/rooms/{roomId}/messages", TestContext.Current.CancellationToken))!;
        Assert.DoesNotContain(history.Messages, m => m.Seq == ack.Seq);
    }

    [Fact]
    public async Task MessagesDelete_AsAppRole_IsDenied()
    {
        // The only erasure path is redact_message(); a direct DELETE as dotwire_app must
        // fail at the grant layer (trap #2: owner bypasses grants, so test as the app role).
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        await using var conn = new NpgsqlConnection(
            $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = new NpgsqlCommand("DELETE FROM messages WHERE room_id = $1", conn);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    private async Task<int> WaitForAuditRowCountAsync(string eventType, string actorId, int expectedAtLeast)
    {
        for (var i = 0; i < 40; i++)
        {
            await using var conn = await OpenOwnerConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE event_type = $1 AND actor_id = $2", conn);
            cmd.Parameters.AddWithValue(eventType);
            cmd.Parameters.AddWithValue(actorId);
            var count = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            if (count >= expectedAtLeast)
                return (int)count;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"expected at least {expectedAtLeast} '{eventType}' audit_log rows for actor {actorId} within 10s");
        return 0;
    }

    private async Task<string?> GetUserRoleAsync(string userId)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT role FROM user_roles WHERE user_id = $1", conn);
        cmd.Parameters.AddWithValue(userId);
        return (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AdminInjection_PublishesLiveMessage_AndAuditsWithSenderIdSubject()
    {
        var roomId = Guid.NewGuid();
        var member = $"it-user-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomId);
        await SeedUserAsync(admin, "admin");

        await using var connection = BuildHubConnection(TestTokens.Mint(member));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var messageTcs = new TaskCompletionSource<RoomMessageDelivery>();
        connection.On<RoomMessageDelivery>("ReceiveMessage", msg => messageTcs.TrySetResult(msg));
        await connection.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var inject = await adminClient.PostAsJsonAsync(
            $"/admin/rooms/{roomId}/messages", new { content = "injected by an admin" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, inject.StatusCode);
        var ack = (await inject.Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken))!;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);
        var received = await messageTcs.Task.WaitAsync(linkedCts.Token);
        Assert.Equal(roomId, received.RoomId);
        Assert.Equal(ack.Seq, received.Seq);
        Assert.Equal("system", received.SenderId);
        Assert.Equal("injected by an admin", received.Content);

        await WaitForAuditRowCountAsync("message.injected", admin, 1);
        await using (var conn = await OpenOwnerConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "SELECT room_id, message_seq, subject_id FROM audit_log WHERE event_type = 'message.injected' AND actor_id = $1", conn))
        {
            cmd.Parameters.AddWithValue(admin);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(roomId, reader.GetGuid(0));
            Assert.Equal((long)ack.Seq, reader.GetInt64(1));
            Assert.Equal("system", reader.GetString(2));
        }

        await connection.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureMemberRole_GrantsMember_ButNeverOverwritesExistingRole()
    {
        var roomId = Guid.NewGuid();
        var admin = $"it-admin-{Guid.NewGuid():N}";
        var freshUser = $"it-user-fresh-{Guid.NewGuid():N}";
        var auditorUser = $"it-user-auditor-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");
        await SeedUserAsync(auditorUser, "auditor");

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var response = await adminClient.PostAsJsonAsync(
            $"/admin/rooms/{roomId}/members",
            new AddMembersRequest([freshUser, auditorUser], EnsureMemberRole: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<RoomMembersResponse>(TestContext.Current.CancellationToken))!;
        Assert.Contains(freshUser, body.Members);
        Assert.Contains(auditorUser, body.Members);

        Assert.Equal("member", await GetUserRoleAsync(freshUser));
        Assert.Equal("auditor", await GetUserRoleAsync(auditorUser)); // never overwritten

        await WaitForAuditRowCountAsync("member.added", admin, 2);
    }

    [Fact]
    public async Task EnsureMemberRole_Omitted_LeavesRoleTableUntouched()
    {
        var roomId = Guid.NewGuid();
        var admin = $"it-admin-{Guid.NewGuid():N}";
        var freshUser = $"it-user-fresh-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var response = await adminClient.PostAsJsonAsync(
            $"/admin/rooms/{roomId}/members",
            new AddMembersRequest([freshUser]),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Null(await GetUserRoleAsync(freshUser));
    }

    [Fact]
    public async Task MembershipRevocation_RemovesLiveHubSubscriber_AndAudits()
    {
        var roomId = Guid.NewGuid();
        var member = $"it-user-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomId);
        await SeedUserAsync(admin, "admin");

        await using var connection = BuildHubConnection(TestTokens.Mint(member));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var revokedTcs = new TaskCompletionSource<MembershipRevoked>();
        connection.On<MembershipRevoked>("MembershipRevoked", r => revokedTcs.TrySetResult(r));
        await connection.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var revoke = await adminClient.DeleteAsync(
            $"/admin/rooms/{roomId}/members/{member}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);
        var revoked = await revokedTcs.Task.WaitAsync(linkedCts.Token);
        Assert.Equal(roomId, revoked.RoomId);

        await WaitForAuditRowCountAsync("member.removed", admin, 1);

        await connection.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RoleDelete_PublishesRevocationPerRoom_AndAuditsRoleDeleted()
    {
        var roomA = Guid.NewGuid();
        var roomB = Guid.NewGuid();
        var member = $"it-user-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomA);
        await SeedUserAsync(member, "member", roomB);
        await SeedUserAsync(admin, "admin");

        await using var connection = BuildHubConnection(TestTokens.Mint(member));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var revokedRooms = new HashSet<Guid>();
        var allRevokedTcs = new TaskCompletionSource();
        connection.On<MembershipRevoked>("MembershipRevoked", r =>
        {
            revokedRooms.Add(r.RoomId);
            if (revokedRooms.Count == 2)
                allRevokedTcs.TrySetResult();
        });
        await connection.InvokeAsync("Subscribe", roomA, TestContext.Current.CancellationToken);
        await connection.InvokeAsync("Subscribe", roomB, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var response = await adminClient.DeleteAsync($"/admin/users/{member}/role", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, TestContext.Current.CancellationToken);
        await allRevokedTcs.Task.WaitAsync(linkedCts.Token);
        Assert.Contains(roomA, revokedRooms);
        Assert.Contains(roomB, revokedRooms);

        await WaitForAuditRowCountAsync("role.deleted", admin, 1);
        Assert.Null(await GetUserRoleAsync(member));

        await connection.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("auditor")]
    [InlineData("admin")]
    public async Task RoleSet_EmitsAuditEventForRole(string role)
    {
        var admin = $"it-admin-{Guid.NewGuid():N}";
        var target = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var response = await adminClient.PutAsJsonAsync(
            $"/admin/users/{target}/role", new { role }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitForAuditRowCountAsync($"role.set.{role}", admin, 1);
    }

    private static async IAsyncEnumerable<(string Event, string Data, string? Id)> ReadSseEventsAsync(
        Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? currentEvent = null;
        string? currentData = null;
        string? currentId = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                yield break;

            if (line.Length == 0)
            {
                if (currentEvent is not null || currentData is not null)
                    yield return (currentEvent ?? "message", currentData ?? "", currentId);
                currentEvent = null;
                currentData = null;
                currentId = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                currentEvent = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                currentData = line["data: ".Length..];
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
                currentId = line["id: ".Length..];
            // ": ping" keep-alive comment lines are ignored.
        }
    }

    [Fact]
    public async Task Sse_Disabled_Is404()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);

        var disabledFactory = _factory.WithWebHostBuilder(b => b.UseSetting("Dotwire:Sse:Enabled", "false"));
        var client = disabledFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));

        var response = await client.GetAsync($"/rooms/{roomId}/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sse_NoToken_Is401()
    {
        var roomId = Guid.NewGuid();
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/rooms/{roomId}/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sse_ReplayThenLive_WithResume()
    {
        var roomId = Guid.NewGuid();
        var sub = $"it-sse-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);
        var client = Client(TestTokens.Mint(sub));

        var ack1 = await (await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "one" }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken);
        var ack2 = await (await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "two" }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken);

        // SSE replay reads from the `messages` table, which the batch PostgresWriterService
        // fills in asynchronously after the JetStream ack - wait for it, same idiom as
        // History_ReturnsDecryptedMessagesAndSupportsGapFill.
        for (var i = 0; i < 40; i++)
        {
            var page = await client.GetFromJsonAsync<RoomHistoryResponse>(
                $"/rooms/{roomId}/messages", TestContext.Current.CancellationToken);
            if (page is not null && page.Messages.Length >= 2)
                break;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{roomId}/events?afterSeq=0");
        sseRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        await using var stream = await sseResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);
        var enumerator = ReadSseEventsAsync(stream, linkedCts.Token).GetAsyncEnumerator(linkedCts.Token);

        var replayed = new List<(ulong Seq, bool Replayed)>();
        while (replayed.Count < 2)
        {
            Assert.True(await enumerator.MoveNextAsync());
            var (evt, data, id) = enumerator.Current;
            if (evt != "message")
                continue;
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            var seq = ulong.Parse(id!);
            var replayedFlag = doc.RootElement.TryGetProperty("replayed", out var r) && r.GetBoolean();
            replayed.Add((seq, replayedFlag));
        }

        Assert.Equal(new[] { ack1!.Seq, ack2!.Seq }, replayed.Select(r => r.Seq).ToArray());
        Assert.All(replayed, r => Assert.True(r.Replayed));

        await Task.Delay(200, TestContext.Current.CancellationToken); // let the NATS live subscription register

        // Now a live message should arrive without the replayed flag.
        var ack3 = await (await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "three" }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<SendAck>(TestContext.Current.CancellationToken);

        (string Event, string Data, string? Id) liveEvt = default;
        while (true)
        {
            Assert.True(await enumerator.MoveNextAsync());
            liveEvt = enumerator.Current;
            if (liveEvt.Event == "message")
                break;
        }

        using var liveDoc = System.Text.Json.JsonDocument.Parse(liveEvt.Data);
        Assert.Equal(ack3!.Seq, ulong.Parse(liveEvt.Id!));
        var liveReplayedFlag = liveDoc.RootElement.TryGetProperty("replayed", out var lr) && lr.GetBoolean();
        Assert.False(liveReplayedFlag);
    }

    [Fact]
    public async Task Sse_Revocation_ClosesStream()
    {
        var roomId = Guid.NewGuid();
        var member = $"it-sse-revoke-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomId);
        await SeedUserAsync(admin, "admin");
        var client = Client(TestTokens.Mint(member));

        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{roomId}/events");
        sseRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(member));
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        await using var stream = await sseResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, TestContext.Current.CancellationToken);
        var enumerator = ReadSseEventsAsync(stream, linkedCts.Token).GetAsyncEnumerator(linkedCts.Token);

        await Task.Delay(200, TestContext.Current.CancellationToken); // let the SSE subscription register

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var response = await adminClient.DeleteAsync($"/admin/rooms/{roomId}/members/{member}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        (string Event, string Data, string? Id) revokedEvt = default;
        while (true)
        {
            Assert.True(await enumerator.MoveNextAsync());
            revokedEvt = enumerator.Current;
            if (revokedEvt.Event == "revoked")
                break;
        }

        // The server closes the stream after `revoked` (spec §3.5/§3.9): no further events follow.
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task RateLimit_Send_BurstThenRateLimited_WithRetryAfter()
    {
        // Default Send bucket: burst 20, refill 5/s (spec §3.10) - 21 rapid sends must trip it.
        var roomId = Guid.NewGuid();
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member", roomId);
        var client = Client(TestTokens.Mint(sub));

        HttpResponseMessage? limited = null;
        for (var i = 0; i < 25 && limited is null; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/rooms/{roomId}/messages", new { content = $"burst {i}" }, TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                limited = response;
        }

        Assert.NotNull(limited);
        Assert.True(limited!.Headers.RetryAfter is not null || limited.Headers.Contains("Retry-After"));
        var body = await limited.Content.ReadFromJsonAsync<ApiErrorResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("rate_limited", body!.Error);
    }

    [Fact]
    public async Task RateLimit_Hub_ThrowsRateLimitedAfterBurst()
    {
        // Default Hub bucket: burst 30, refill 10/s (spec §3.10) - applies to Subscribe/Unsubscribe
        // regardless of membership outcome, since the filter runs before the hub method body.
        var sub = $"it-user-{Guid.NewGuid():N}";
        await SeedUserAsync(sub, "member");
        await using var connection = BuildHubConnection(TestTokens.Mint(sub));
        await connection.StartAsync(TestContext.Current.CancellationToken);

        HubException? limited = null;
        for (var i = 0; i < 40 && limited is null; i++)
        {
            try
            {
                await connection.InvokeAsync("Subscribe", Guid.NewGuid(), TestContext.Current.CancellationToken);
            }
            catch (HubException ex) when (ex.Message.Contains("RateLimited"))
            {
                limited = ex;
            }
            catch (HubException)
            {
                // Forbidden (not a member of the random room) - expected, keep burning budget.
            }
        }

        Assert.NotNull(limited);
    }

    [Fact]
    public async Task Retention_RoundTrips_ThroughApi_AndTimescaleJobs()
    {
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");
        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));

        var putResponse = await adminClient.PutAsJsonAsync(
            "/admin/retention", new { messagesDays = 30 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getResponse = await adminClient.GetAsync("/admin/retention", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var body = await getResponse.Content.ReadFromJsonAsync<RetentionResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(30, body!.MessagesDays);

        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT get_message_retention()", conn);
        var days = (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(30, days);

        // Restore indefinite retention so later tests in this fixture aren't affected.
        await adminClient.PutAsJsonAsync("/admin/retention", new { messagesDays = 0 }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Retention_OutOfBounds_Is400()
    {
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(admin, "admin");
        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));

        var response = await adminClient.PutAsJsonAsync(
            "/admin/retention", new { messagesDays = -1 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await adminClient.PutAsJsonAsync(
            "/admin/retention", new { messagesDays = 36501 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DsarExport_StreamsMessagesRoomsAndAuditEvents_AuditsItself()
    {
        var roomId = Guid.NewGuid();
        var member = $"it-user-{Guid.NewGuid():N}";
        var admin = $"it-admin-{Guid.NewGuid():N}";
        await SeedUserAsync(member, "member", roomId);
        await SeedUserAsync(admin, "admin");

        var memberClient = Client(TestTokens.Mint(member));
        var send = await memberClient.PostAsJsonAsync(
            $"/rooms/{roomId}/messages", new { content = "dsar target message" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);

        // Wait for the batch writer to land the message so the export query sees it.
        await using (var conn = await OpenOwnerConnectionAsync())
        {
            for (var i = 0; i < 40; i++)
            {
                await using var check = new NpgsqlCommand("SELECT count(*) FROM messages WHERE sender_id = $1", conn);
                check.Parameters.AddWithValue(member);
                if ((long)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0)
                    break;
                await Task.Delay(250, TestContext.Current.CancellationToken);
            }
        }

        var adminClient = Client(TestTokens.Mint(admin, role: "admin"));
        var exportResponse = await adminClient.GetAsync($"/admin/users/{member}/export", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);

        using var doc = System.Text.Json.JsonDocument.Parse(
            await exportResponse.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal(member, root.GetProperty("userId").GetString());
        Assert.Equal("member", root.GetProperty("role").GetString());

        var roomIds = root.GetProperty("roomIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(roomId.ToString(), roomIds);

        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(messages, m => m.GetProperty("content").GetString() == "dsar target message");

        var auditEvents = root.GetProperty("auditEvents").EnumerateArray().ToArray();
        Assert.All(auditEvents, e => Assert.False(e.TryGetProperty("hash", out _)));

        // dsar.exported is published before streaming starts but only lands in audit_log once
        // AuditWriterService chains it (async, spec §3.6) - so it can't appear in THIS export's
        // own auditEvents array. Poll the durable table directly to confirm it was recorded.
        var found = false;
        await using (var conn = await OpenOwnerConnectionAsync())
        {
            for (var i = 0; i < 40 && !found; i++)
            {
                await using var check = new NpgsqlCommand(
                    "SELECT count(*) FROM audit_log WHERE event_type = 'dsar.exported' AND subject_id = $1", conn);
                check.Parameters.AddWithValue(member);
                found = (long)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0;
                if (!found)
                    await Task.Delay(250, TestContext.Current.CancellationToken);
            }
        }
        Assert.True(found, "dsar.exported audit event never landed in audit_log");
    }

    private sealed record SendAck(ulong Seq, DateTimeOffset Time);
}
