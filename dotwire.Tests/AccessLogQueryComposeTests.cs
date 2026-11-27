using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Dotwire.Nats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class AccessLogQueryComposeTests : IDisposable
{
    private static readonly DateTimeOffset From = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(2);

    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _auditor = $"it-auditor-{Guid.NewGuid():N}";
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly WebApplicationFactory<Program> _factory;

    public AccessLogQueryComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");
        DotNetEnv.Env.TraversePath().Load();
        var ownerPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={ownerPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}";
        _factory = NewFactory();
        _ = _factory.Services;
        ExecAsync(
            "INSERT INTO user_roles (user_id, role) VALUES (@u, 'auditor') ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role",
            ("u", _auditor)).GetAwaiter().GetResult();
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
        return factory;
    }

    public void Dispose()
    {
        foreach (var f in _factories)
            f.Dispose();
    }

    private HttpClient Client(WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Mint(_auditor, role: "auditor"));
        return client;
    }

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private Task OpenAsync(Guid room, string user, Guid window, DateTimeOffset at, DateTimeOffset expires, string? actor = null) =>
        ExecAsync(
            "INSERT INTO access_log (time, stream_seq, event, room_id, user_id, actor_id, role, via, window_id, expires_at) " +
            "VALUES (@t, @s, 'live.opened', @r, @u, @a, 'member', 'hub', @w, @e)",
            ("t", at), ("s", Random.Shared.NextInt64()), ("r", room), ("u", user), ("a", actor), ("w", window), ("e", expires));

    private Task CloseAsync(Guid room, string user, Guid window, DateTimeOffset at, string reason = "left") =>
        ExecAsync(
            "INSERT INTO access_log (time, stream_seq, event, room_id, user_id, role, via, window_id, reason) " +
            "VALUES (@t, @s, 'live.closed', @r, @u, 'member', 'hub', @w, @x)",
            ("t", at), ("s", Random.Shared.NextInt64()), ("r", room), ("u", user), ("w", window), ("x", reason));

    private Task HistoryAsync(Guid room, string user, DateTimeOffset at, long seq) =>
        ExecAsync(
            "INSERT INTO access_log (time, stream_seq, event, room_id, user_id, role, via, from_seq, to_seq, count) " +
            "VALUES (@t, @s, 'history.read', @r, @u, 'member', 'rest', 1, 5, 5)",
            ("t", at), ("s", seq), ("r", room), ("u", user));

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.ToString("O"));

    private async Task<AccessLogPageResponse> QueryAsync(Guid room, string extra = "")
    {
        var response = await Client().GetAsync($"/audit/access?from={Iso(From)}&to={Iso(To)}&roomId={room}{extra}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<AccessLogPageResponse>(Ct))!;
    }

    [Fact]
    public async Task WindowOpenedBeforeFrom_ClosedInside_Returned()
    {
        var room = Guid.NewGuid();
        var window = Guid.NewGuid();
        await OpenAsync(room, "u1", window, From.AddMinutes(-30), From.AddMinutes(30));
        await CloseAsync(room, "u1", window, From.AddMinutes(10));

        var page = await QueryAsync(room);

        var entry = Assert.Single(page.Entries);
        Assert.Equal("live", entry.Kind);
        Assert.Equal(window, entry.WindowId);
        Assert.Equal(From.AddMinutes(-30), entry.Start);
        Assert.Equal(From.AddMinutes(10), entry.End);
        Assert.Equal("left", entry.EndReason);
    }

    [Fact]
    public async Task WindowClosedBeforeFrom_NotReturned()
    {
        var room = Guid.NewGuid();
        var window = Guid.NewGuid();
        await OpenAsync(room, "u1", window, From.AddMinutes(-40), From.AddMinutes(20));
        await CloseAsync(room, "u1", window, From.AddMinutes(-10));

        Assert.Empty((await QueryAsync(room)).Entries);
    }

    [Fact]
    public async Task LostClose_ReturnsExpiresAt()
    {
        var room = Guid.NewGuid();
        var window = Guid.NewGuid();
        await OpenAsync(room, "u1", window, From.AddMinutes(-10), From.AddMinutes(50));

        var entry = Assert.Single((await QueryAsync(room)).Entries);

        Assert.Null(entry.End);
        Assert.Null(entry.EndReason);
        Assert.Equal(From.AddMinutes(50), entry.ExpiresAt);
    }

    [Fact]
    public async Task DuplicateCloses_PairToEarliest()
    {
        var room = Guid.NewGuid();
        var window = Guid.NewGuid();
        await OpenAsync(room, "u1", window, From.AddMinutes(5), From.AddMinutes(60));
        await CloseAsync(room, "u1", window, From.AddMinutes(20), "error");
        await CloseAsync(room, "u1", window, From.AddMinutes(8), "disconnected");

        var entry = Assert.Single((await QueryAsync(room)).Entries);

        Assert.Equal(From.AddMinutes(8), entry.End);
        Assert.Equal("disconnected", entry.EndReason);
    }

    [Fact]
    public async Task OrphanClose_Invisible()
    {
        var room = Guid.NewGuid();
        await CloseAsync(room, "u1", Guid.NewGuid(), From.AddMinutes(10));

        Assert.Empty((await QueryAsync(room)).Entries);
    }

    [Fact]
    public async Task HistoryInRange_Returned_OutOfRange_NotReturned()
    {
        var room = Guid.NewGuid();
        await HistoryAsync(room, "u1", From.AddMinutes(5), Random.Shared.NextInt64());
        await HistoryAsync(room, "u1", From.AddMinutes(-5), Random.Shared.NextInt64());
        await HistoryAsync(room, "u1", To.AddMinutes(5), Random.Shared.NextInt64());

        var entry = Assert.Single((await QueryAsync(room)).Entries);

        Assert.Equal("history", entry.Kind);
        Assert.Equal(From.AddMinutes(5), entry.Start);
        Assert.Equal(1UL, entry.FromSeq);
        Assert.Equal(5UL, entry.ToSeq);
        Assert.Equal(5, entry.Count);
    }

    [Fact]
    public async Task FilterByUser_And_ByRoom()
    {
        var roomA = Guid.NewGuid();
        var roomB = Guid.NewGuid();
        var userA = $"ua-{Guid.NewGuid():N}";
        var userB = $"ub-{Guid.NewGuid():N}";
        await HistoryAsync(roomA, userA, From.AddMinutes(1), Random.Shared.NextInt64());
        await HistoryAsync(roomA, userB, From.AddMinutes(2), Random.Shared.NextInt64());
        await HistoryAsync(roomB, userA, From.AddMinutes(3), Random.Shared.NextInt64());

        var byRoomAndUser = await QueryAsync(roomA, $"&userId={userA}");
        var byUser = await Client().GetFromJsonAsync<AccessLogPageResponse>(
            $"/audit/access?from={Iso(From)}&to={Iso(To)}&userId={userA}", Ct);

        var one = Assert.Single(byRoomAndUser.Entries);
        Assert.Equal((roomA, userA), (one.RoomId, one.UserId));
        Assert.Equal(new[] { roomA, roomB }.Order(), byUser!.Entries.Select(e => e.RoomId).Order());
        Assert.All(byUser.Entries, e => Assert.Equal(userA, e.UserId));
    }

    [Fact]
    public async Task Pagination_CursorCoversAllWithoutDuplicates()
    {
        var room = Guid.NewGuid();
        var baseSeq = Random.Shared.NextInt64(1, long.MaxValue / 2);
        for (var i = 0; i < 250; i++)
            await HistoryAsync(room, $"u{i}", From.AddSeconds(i / 7), baseSeq + i);

        var users = new List<string>();
        var starts = new List<DateTimeOffset>();
        var pages = 0;
        string? cursor = null;
        do
        {
            var page = await QueryAsync(room, "&limit=100" + (cursor is null ? "" : $"&cursor={cursor}"));
            pages++;
            users.AddRange(page.Entries.Select(e => e.UserId));
            starts.AddRange(page.Entries.Select(e => e.Start));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(starts[99], starts[100]);
        Assert.Equal(starts[199], starts[200]);
        Assert.Equal(250, users.Count);
        Assert.Equal(250, users.Distinct().Count());
        Assert.Equal(starts.Order(), starts);
    }

    [Fact]
    public async Task LateClose_AfterExpiresPlusSkew_ReadsAsOpen()
    {
        var room = Guid.NewGuid();
        var window = Guid.NewGuid();
        await OpenAsync(room, "u1", window, From.AddMinutes(5), From.AddMinutes(20));
        await CloseAsync(room, "u1", window, From.AddMinutes(22));

        var entry = Assert.Single((await QueryAsync(room)).Entries);

        Assert.Null(entry.End);
        Assert.Null(entry.EndReason);
        Assert.Equal(From.AddMinutes(20), entry.ExpiresAt);
    }

    [Fact]
    public async Task OpenExpiredBeforeFrom_Excluded()
    {
        var room = Guid.NewGuid();
        await OpenAsync(room, "u1", Guid.NewGuid(), From.AddMinutes(-30), From.AddMinutes(-1));

        Assert.Empty((await QueryAsync(room)).Entries);
    }

    [Fact]
    public async Task Read_EmitsAccessLogReadAudit()
    {
        var room = Guid.NewGuid();
        var user = $"subj-{Guid.NewGuid():N}";
        await QueryAsync(room, $"&userId={user}");

        for (var i = 0; i < 80; i++)
        {
            await using var conn = new NpgsqlConnection(_ownerConnectionString);
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT actor_id, room_id, subject_id FROM audit_log WHERE event_type = 'access_log.read' AND room_id = $1", conn);
            cmd.Parameters.AddWithValue(room);
            await using var reader = await cmd.ExecuteReaderAsync(Ct);
            if (await reader.ReadAsync(Ct))
            {
                Assert.Equal(_auditor, reader.GetString(0));
                Assert.Equal(room, reader.GetGuid(1));
                Assert.Equal(user, reader.GetString(2));
                return;
            }
            await Task.Delay(250, Ct);
        }
        Assert.Fail("no access_log.read audit row within 20s");
    }

    [Fact]
    public async Task AuditPublishFails_503()
    {
        var failing = NewFactory(b => b.ConfigureTestServices(s => s.AddSingleton<AuditPublisher>(new ThrowingAuditPublisher())));

        var response = await Client(failing).GetAsync($"/audit/access?from={Iso(From)}&roomId={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("audit_unavailable", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
    }

    [Theory]
    [InlineData("to_before_from")]
    [InlineData("no_from")]
    [InlineData("bad_cursor")]
    [InlineData("bad_cursor_overflow")]
    [InlineData("bad_room")]
    [InlineData("bad_from")]
    public async Task InvalidParameters_400InvalidRequest(string kind)
    {
        var query = kind switch
        {
            "to_before_from" => $"from={Iso(To)}&to={Iso(From)}",
            "no_from" => $"to={Iso(To)}",
            "bad_cursor" => $"from={Iso(From)}&cursor=abc",
            "bad_cursor_overflow" => $"from={Iso(From)}&cursor=99999999999999999999.1",
            "bad_room" => $"from={Iso(From)}&roomId=nope",
            _ => "from=yesterday-ish",
        };

        var response = await Client().GetAsync($"/audit/access?{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
    }

    [Fact]
    public async Task InvalidUserId_400()
    {
        var response = await Client().GetAsync($"/audit/access?from={Iso(From)}&userId=bad%0Aid", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_user_id", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
    }
}
