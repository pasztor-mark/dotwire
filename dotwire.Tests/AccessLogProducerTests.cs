using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dotwire.Api;
using Dotwire.Crypto;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class AccessLogProducerTests : IAsyncDisposable
{
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RecordingAccessLogPublisher _log = new();
    private readonly Guid _room = Guid.NewGuid();
    private readonly string _user = $"it-alp-{Guid.NewGuid():N}";

    public AccessLogProducerTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        var app = _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", app);
            b.UseSetting("ConnectionStrings:PostgresRead", app);
            b.UseSetting("Postgres:AppRolePassword", appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Enabled", "false");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            b.UseSetting("Dotwire:RateLimits:Enabled", "false");
            b.ConfigureTestServices(s => s.AddSingleton<AccessLogPublisher>(_log));
        });
        _ = _factory.Services;
        SeedMemberAsync().GetAwaiter().GetResult();
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
        await _factory.DisposeAsync();
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(CancellationToken.None);
        foreach (var sql in new[]
        {
            "DELETE FROM room_members WHERE room_id = $1",
            "DELETE FROM messages WHERE room_id = $1",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(_room);
            try { await cmd.ExecuteNonQueryAsync(CancellationToken.None); } catch (PostgresException) { }
        }
        await using var roles = new NpgsqlCommand("DELETE FROM user_roles WHERE user_id = $1", conn);
        roles.Parameters.AddWithValue(_user);
        try { await roles.ExecuteNonQueryAsync(CancellationToken.None); } catch (PostgresException) { }
    }

    private async Task SeedMemberAsync()
    {
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role) VALUES ($1, 'member') ON CONFLICT (user_id) DO NOTHING", conn))
        {
            cmd.Parameters.AddWithValue(_user);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await using var member = new NpgsqlCommand("INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
        member.Parameters.AddWithValue(_room);
        member.Parameters.AddWithValue(_user);
        await member.ExecuteNonQueryAsync(Ct);
    }

    public class HungJs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetStreamAsync")
                throw new NotSupportedException(targetMethod?.Name);
            var token = args!.OfType<CancellationToken>().Last();
            return Hang(token);
        }

        private static async ValueTask<INatsJSStream> Hang(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        }
    }

    [Fact]
    public async Task HistoryTail_NatsHung_FallsBackToPostgresWithinDeadline()
    {
        await SeedMessagesAsync(1);
        await using var ds = NpgsqlDataSource.Create(_appConnectionString);
        var streams = new RoomsStreamCache(DispatchProxy.Create<INatsJSContext, HungJs>(), Options.Create(new NatsOptions { Enabled = true }));
        var nats = new NatsOptions { Enabled = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var call = Messages.QueryHistoryPageAsync(ds, streams, nats, NullLogger.Instance, _room, null, null, 50, cts.Token);
        Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(8), Ct)));

        var (rows, hasMore) = await call;
        Assert.Equal(1UL, Assert.Single(rows).Seq);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task HistoryTail_RequestCancelled_StillThrows()
    {
        await SeedMessagesAsync(1);
        await using var ds = NpgsqlDataSource.Create(_appConnectionString);
        var streams = new RoomsStreamCache(DispatchProxy.Create<INatsJSContext, HungJs>(), Options.Create(new NatsOptions { Enabled = true }));
        var nats = new NatsOptions { Enabled = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Messages.QueryHistoryPageAsync(ds, streams, nats, NullLogger.Instance, _room, null, null, 50, cts.Token));
    }

    private async Task SeedMessagesAsync(int count)
    {
        var cipher = _factory.Services.GetRequiredService<MessageCipher>();
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        for (var i = 1; i <= count; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO messages (room_id, time, seq, sender_id, key_id, content) " +
                "VALUES ($1, now() - interval '1 hour' + make_interval(secs => $2), $3, $4, 'it', $5)", conn);
            cmd.Parameters.AddWithValue(_room);
            cmd.Parameters.AddWithValue((double)i);
            cmd.Parameters.AddWithValue((long)i);
            cmd.Parameters.AddWithValue(_user);
            cmd.Parameters.AddWithValue(cipher.Encrypt(Encoding.UTF8.GetBytes($"m{i}"), _room, _user));
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    private HttpClient Client(string? actor = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Mint(_user, actor: actor));
        return client;
    }

    private static async Task<List<(string Event, string? Id)>> ReadEventsAsync(Stream stream, int messages, CancellationToken ct)
    {
        var events = new List<(string, string?)>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null;
        string? name = null;
        var seen = 0;
        while (seen < messages)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
                name = line[7..];
            else if (line.Length == 0 && name is not null)
            {
                events.Add((name, id));
                if (name == "message")
                    seen++;
                name = null;
                id = null;
            }
        }
        return events;
    }

    private async Task<HttpResponseMessage> OpenSseAsync(HttpClient client, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{_room}/events{query}");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
    }

    [Fact]
    public async Task History_RecordsPageRange()
    {
        await SeedMessagesAsync(5);

        var page = await Client().GetFromJsonAsync<RoomHistoryResponse>($"/rooms/{_room}/messages?afterSeq=1&limit=3", Ct);

        Assert.Equal(new ulong[] { 2, 3, 4 }, page!.Messages.Select(m => m.Seq).ToArray());
        var read = Assert.Single(_log.Recorded);
        Assert.Equal(AccessEvents.HistoryRead, read.Event);
        Assert.Equal(AccessEvents.Rest, read.Via);
        Assert.Equal(2UL, read.FromSeq);
        Assert.Equal(4UL, read.ToSeq);
        Assert.Equal(3, read.Count);
        Assert.Equal(_room, read.RoomId);
        Assert.Equal(_user, read.UserId);
        Assert.Equal("member", read.Role);
    }

    [Fact]
    public async Task History_EmptyPage_RecordsCountZero()
    {
        var page = await Client().GetFromJsonAsync<RoomHistoryResponse>($"/rooms/{_room}/messages", Ct);

        Assert.Empty(page!.Messages);
        var read = Assert.Single(_log.Recorded);
        Assert.Equal(0, read.Count);
        Assert.Null(read.FromSeq);
        Assert.Null(read.ToSeq);
    }

    [Fact]
    public async Task History_FailingPublisher_Returns503_NoMessages()
    {
        await SeedMessagesAsync(3);
        _log.FailRecords = true;

        using var response = await Client().GetAsync($"/rooms/{_room}/messages", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal("access_log_unavailable", System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("m1", body);
    }

    [Fact]
    public async Task History_ActorClaim_Recorded()
    {
        await SeedMessagesAsync(1);

        using var response = await Client(actor: "host-admin").GetAsync($"/rooms/{_room}/messages", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("host-admin", Assert.Single(_log.Recorded).ActorId);
    }

    [Fact]
    public async Task Sse_Open_RecordedBeforeStream_FailingPublisher_503()
    {
        _log.FailRecords = true;

        using var response = await OpenSseAsync(Client());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("access_log_unavailable", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
        var open = Assert.Single(_log.Recorded);
        Assert.Equal(AccessEvents.Opened, open.Event);
        Assert.Equal(AccessEvents.Sse, open.Via);
        Assert.Empty(_factory.Services.GetRequiredService<Dotwire.Realtime.RoomInterestManager>().LiveSubscriptions());
    }

    [Fact]
    public async Task Sse_Open_Succeeds_RecordsOpenOnce()
    {
        using var response = await OpenSseAsync(Client());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var open = Assert.Single(_log.Recorded);
        Assert.Equal(AccessEvents.Opened, open.Event);
        Assert.Equal(AccessEvents.Sse, open.Via);
        Assert.NotNull(open.WindowId);
    }

    [Fact]
    public async Task Sse_Replay250_Page100_ThreeReads()
    {
        await SeedMessagesAsync(250);
        using var response = await OpenSseAsync(Client(), "?afterSeq=0");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var events = await ReadEventsAsync(await response.Content.ReadAsStreamAsync(Ct), 250, cts.Token);

        Assert.Equal(250, events.Count(e => e.Event == "message"));
        Assert.Equal(AccessEvents.Opened, _log.Recorded[0].Event);
        var reads = _log.Recorded.Skip(1).ToList();
        Assert.All(reads, r => Assert.Equal(AccessEvents.HistoryRead, r.Event));
        Assert.All(reads, r => Assert.Equal(AccessEvents.Sse, r.Via));
        Assert.Equal(
            new[] { (1UL, 100UL, 100), (101UL, 200UL, 100), (201UL, 250UL, 50) },
            reads.Select(r => (r.FromSeq!.Value, r.ToSeq!.Value, r.Count!.Value)).ToArray());
    }

    [Fact]
    public async Task Sse_ReplayFailsOnPage2_StreamEndsAfterPage1()
    {
        await SeedMessagesAsync(250);
        _log.FailFromCall = 3;
        using var response = await OpenSseAsync(Client(), "?afterSeq=0");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var events = await ReadEventsAsync(await response.Content.ReadAsStreamAsync(Ct), 250, cts.Token);

        var seqs = events.Where(e => e.Event == "message").Select(e => ulong.Parse(e.Id!)).ToArray();
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (ulong)i).ToArray(), seqs);
        Assert.Equal(3, _log.Recorded.Count);
    }

    [Fact]
    public async Task Sse_Disconnect_WritesDisconnectedClose()
    {
        using var response = await OpenSseAsync(Client());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var open = Assert.Single(_log.Recorded);

        response.Dispose();
        await WaitAsync(() => _log.Closes.Count > 0);

        var close = Assert.Single(_log.Closes);
        Assert.Equal(AccessEvents.Disconnected, close.Reason);
        Assert.Equal(open.WindowId, close.WindowId);
    }
}
