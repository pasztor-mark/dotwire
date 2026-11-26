using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Crypto;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class ErasureWorkerComposeTests : IAsyncDisposable
{
    private const string Erased = "dotwire:erased";

    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;
    private readonly string _noticeStream;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly List<long> _placed = [];
    private readonly ConcurrentQueue<ErasureNotice> _notices = new();
    private readonly NoticeListener _listener;
    private bool _compressionEnabled;

    public ErasureWorkerComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        _appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={_appPassword}";
        _noticeStream = $"ERASURES_W{Guid.NewGuid():N}";
        _listener = new NoticeListener(body =>
        {
            if (JsonSerializer.Deserialize(body, DotwireJsonContext.Default.ErasureNotice) is { } notice)
                _notices.Enqueue(notice);
        });
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
        var suffix = _noticeStream["ERASURES_W".Length..].ToLowerInvariant();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", _appConnectionString);
            b.UseSetting("ConnectionStrings:PostgresRead", _appConnectionString);
            b.UseSetting("Postgres:AppRolePassword", _appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://127.0.0.1:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Nats:ErasureNoticesStream", _noticeStream);
            b.UseSetting("Nats:ErasureNoticesSubject", $"dotwire.erasures.w{suffix}");
            b.UseSetting("Nats:ErasureNoticesConsumer", $"erasure-notifier-w{suffix}");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            b.UseSetting("Dotwire:RateLimits:Enabled", "false");
            b.UseSetting("Dotwire:MembershipSweepSeconds", "1");
            b.UseSetting("Dotwire:Compliance:Erasure:RecheckMinutes", "1");
            b.UseSetting("Dotwire:Webhooks:Erasure:Url", _listener.Url);
            configure?.Invoke(b);
        });
        _factories.Add(factory);
        _ = factory.Services;
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_placed.Count > 0)
            {
                await using var conn = await OpenOwnerAsync(CancellationToken.None);
                await using var release = new NpgsqlCommand(
                    "INSERT INTO legal_hold_releases (hold_id, released_by) " +
                    "SELECT h.id, 'test-cleanup' FROM unnest($1::bigint[]) AS h(id) " +
                    "WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)", conn);
                release.Parameters.AddWithValue(_placed.ToArray());
                await release.ExecuteNonQueryAsync(CancellationToken.None);
                await using var sync = new NpgsqlCommand("SELECT sync_retention_schedule()", conn);
                await sync.ExecuteNonQueryAsync(CancellationToken.None);
            }

            if (_compressionEnabled)
            {
                await using var conn = await OpenOwnerAsync(CancellationToken.None);
                foreach (var sql in new[]
                {
                    "SELECT remove_compression_policy('messages', if_exists => true)",
                    "SELECT decompress_chunk(c, if_compressed => true) FROM show_chunks('messages') c",
                    "ALTER TABLE messages SET (timescaledb.compress = false)",
                })
                {
                    await using var cmd = new NpgsqlCommand(sql, conn);
                    await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            foreach (var factory in _factories)
                await factory.DisposeAsync();
            await _listener.DisposeAsync();
            await DeleteStreamQuietlyAsync(_noticeStream);
        }
    }

    private static async Task DeleteStreamQuietlyAsync(string stream)
    {
        var connection = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:4222" });
        try
        {
            try
            {
                await new NatsJSContext(connection).DeleteStreamAsync(stream);
            }
            catch (NatsJSApiException)
            {
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private async Task<long> OwnerCountAsync(string sql, params object[] args)
    {
        await using var conn = await OpenOwnerAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task SeedAsync(string sub, string role, params Guid[] rooms)
    {
        await using var conn = await OpenOwnerAsync(Ct);
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

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string sub, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub, role: role));
        return client;
    }

    private async Task<HttpClient> AdminAsync(WebApplicationFactory<Program> factory)
    {
        var admin = $"it-erw-admin-{Guid.NewGuid():N}";
        await SeedAsync(admin, "admin");
        return ClientFor(factory, admin, "admin");
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<List<ulong>> SendAsync(HttpClient client, Guid room, string prefix, int count)
    {
        var seqs = new List<ulong>();
        for (var i = 0; i < count; i++)
        {
            var response = await client.PostAsJsonAsync($"/rooms/{room}/messages", new { content = $"{prefix} {i}" }, Ct);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            seqs.Add((await response.Content.ReadFromJsonAsync<SendMessageResponse>(Ct))!.Seq);
        }

        return seqs;
    }

    private async Task WaitPersistedAsync(Guid room, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1", room) < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "messages never reached Postgres");
            await Task.Delay(200, Ct);
        }
    }

    private static async Task<ErasureStatus> EraseAsync(HttpClient admin, string user, string? mode = null)
    {
        var response = await admin.PostAsJsonAsync($"/admin/users/{user}/erase", new EraseUserRequest(mode), Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ErasureStatus>(Ct))!;
    }

    private static async Task<ErasureStatus> WaitStatusAsync(HttpClient admin, long id, Func<ErasureStatus, bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        ErasureStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await admin.GetFromJsonAsync<ErasureStatus>($"/admin/erasures/{id}", Ct);
            if (done(status!))
                return status!;
            await Task.Delay(250, Ct);
        }

        Assert.Fail($"erasure {id} never became {what}; last status {status?.Status} remaining {status?.Remaining}");
        return status!;
    }

    private static Task<ErasureStatus> WaitCompletedAsync(HttpClient admin, long id) =>
        WaitStatusAsync(admin, id, s => s.Status == "completed", "completed");

    private async Task AssertGoneFromJetStreamAsync(IEnumerable<ulong> seqs)
    {
        var js = _factories[0].Services.GetRequiredService<INatsJSContext>();
        var stream = await js.GetStreamAsync("ROOMS", cancellationToken: Ct);
        foreach (var seq in seqs)
        {
            var ex = await Assert.ThrowsAsync<NatsJSApiException>(async () =>
                await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, Ct));
            Assert.Equal(10037, ex.Error.ErrCode);
        }
    }

    private async Task<long> PlaceHoldAsync(HttpClient admin, Guid room)
    {
        var response = await admin.PostAsJsonAsync("/admin/holds", new { roomId = room, matterRef = "matter-" + Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<LegalHold>(Ct))!.Id;
        _placed.Add(id);
        return id;
    }

    private async Task WaitNoticesAsync(Func<ErasureNotice[], bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done(_notices.ToArray()))
        {
            Assert.True(DateTime.UtcNow < deadline, "expected erasure notices never arrived");
            await Task.Delay(200, Ct);
        }
    }

    private async Task AssertSingleErasedAuditAsync(string user, long value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await OwnerCountAsync("SELECT count(*) FROM audit_log WHERE event_type = 'user.erased' AND subject_id = $1", user) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "user.erased audit row never arrived");
            await Task.Delay(200, Ct);
        }

        await Task.Delay(1500, Ct);
        Assert.Equal(1, await OwnerCountAsync("SELECT count(*) FROM audit_log WHERE event_type = 'user.erased' AND subject_id = $1", user));
        Assert.Equal(1, await OwnerCountAsync(
            "SELECT count(*) FROM audit_log WHERE event_type = 'user.erased' AND subject_id = $1 AND value = $2 AND room_id IS NULL AND message_seq IS NULL",
            user, value));
    }

    [Fact]
    public async Task Anonymize_ReattributesAll_AndHistoryStillDecrypts()
    {
        var factory = NewFactory();
        var admin = await AdminAsync(factory);
        var room = Guid.NewGuid();
        var user = NewId("it-erw-user");
        var reader = NewId("it-erw-reader");
        await SeedAsync(user, "member", room);
        await SeedAsync(reader, "member", room);
        var seqs = await SendAsync(ClientFor(factory, user, "member"), room, "anon", 3);
        await WaitPersistedAsync(room, 3);

        var status = await EraseAsync(admin, user);
        await WaitCompletedAsync(admin, status.Id);

        Assert.Equal(3, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND sender_id = $2", room, Erased));
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM messages WHERE sender_id = $1", user));
        var history = await ClientFor(factory, reader, "member").GetFromJsonAsync<RoomHistoryResponse>($"/rooms/{room}/messages", Ct);
        Assert.Equal(3, history!.Messages.Length);
        Assert.All(history.Messages, m => Assert.Equal(Erased, m.SenderId));
        Assert.Equal(["anon 0", "anon 1", "anon 2"], history.Messages.OrderBy(m => m.Seq).Select(m => m.Content).ToArray());
        await AssertGoneFromJetStreamAsync(seqs);
        await AssertSingleErasedAuditAsync(user, 1);

        var export = await admin.GetStringAsync($"/admin/users/{user}/export", Ct);
        using (var doc = JsonDocument.Parse(export))
            Assert.Equal(0, doc.RootElement.GetProperty("messages").GetArrayLength());

        await WaitNoticesAsync(n => n.Any(x => x.Event == "user.erased" && x.Status == "completed" && x.ErasureId == status.Id));
        var all = _notices.ToArray().Where(n => n.ErasureId == status.Id).ToArray();
        var partial = all.Where(n => n.Status == "partial").ToArray();
        Assert.NotEmpty(partial);
        Assert.All(partial, n => Assert.Equal("anonymize", n.Mode));
        var listed = partial.SelectMany(n => n.Messages!).Where(m => m.RoomId == room).Select(m => m.Seq).ToHashSet();
        Assert.Equal(seqs.ToHashSet(), listed);
        Assert.Single(all, n => n.Status == "completed");
        Assert.Null(all.Single(n => n.Status == "completed").Messages);
    }

    [Fact]
    public async Task Delete_RemovesRows_AndTombstones()
    {
        var factory = NewFactory();
        var admin = await AdminAsync(factory);
        var room = Guid.NewGuid();
        var user = NewId("it-erw-user");
        await SeedAsync(user, "member", room);
        var seqs = await SendAsync(ClientFor(factory, user, "member"), room, "del", 3);
        await WaitPersistedAsync(room, 3);

        var status = await EraseAsync(admin, user, "delete");
        await WaitCompletedAsync(admin, status.Id);

        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1", room));
        Assert.Equal(3, await OwnerCountAsync("SELECT count(*) FROM message_redactions WHERE room_id = $1", room));
        await AssertGoneFromJetStreamAsync(seqs);
        await AssertSingleErasedAuditAsync(user, 2);
    }

    [Fact]
    public async Task HeldRoom_WaitsThenCompletesAfterRelease()
    {
        var factory = NewFactory();
        var admin = await AdminAsync(factory);
        var heldRoom = Guid.NewGuid();
        var openRoom = Guid.NewGuid();
        var user = NewId("it-erw-user");
        await SeedAsync(user, "member", heldRoom, openRoom);
        var client = ClientFor(factory, user, "member");
        await SendAsync(client, heldRoom, "held", 2);
        await SendAsync(client, openRoom, "open", 2);
        await WaitPersistedAsync(heldRoom, 2);
        await WaitPersistedAsync(openRoom, 2);
        var hold = await PlaceHoldAsync(admin, heldRoom);

        var status = await EraseAsync(admin, user);
        var waiting = await WaitStatusAsync(admin, status.Id, s => s.Status == "waiting", "waiting");

        Assert.Contains("legal_hold", waiting.BlockedBy);
        Assert.Equal(2, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND sender_id = $2", openRoom, Erased));
        Assert.Equal(2, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND sender_id = $2", heldRoom, user));

        var release = await admin.DeleteAsync($"/admin/holds/{hold}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        await WaitCompletedAsync(admin, status.Id);

        Assert.Equal(2, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND sender_id = $2", heldRoom, Erased));
    }

    [Fact]
    public async Task CompressedHistory_Anonymize_Works()
    {
        var factory = NewFactory(b => b.UseSetting("Dotwire:Compression:AfterDays", "3"));
        _compressionEnabled = true;
        var admin = await AdminAsync(factory);
        var cipher = factory.Services.GetRequiredService<MessageCipher>();
        var room = Guid.NewGuid();
        var user = NewId("it-erw-user");
        var reader = NewId("it-erw-reader");
        await SeedAsync(reader, "member", room);
        var plain = new[] { "old 0", "old 1", "old 2" };
        await using (var conn = await OpenOwnerAsync(Ct))
        {
            for (var i = 0; i < plain.Length; i++)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO messages (room_id, time, seq, sender_id, key_id, content) " +
                    "VALUES ($1, now() - interval '10 days' + make_interval(secs => $2), $3, $4, 'it', $5)", conn);
                insert.Parameters.AddWithValue(room);
                insert.Parameters.AddWithValue((double)i);
                insert.Parameters.AddWithValue((long)i + 1);
                insert.Parameters.AddWithValue(user);
                insert.Parameters.AddWithValue(cipher.Encrypt(Encoding.UTF8.GetBytes(plain[i]), room, user));
                await insert.ExecuteNonQueryAsync(Ct);
            }

            await using var compress = new NpgsqlCommand(
                "SELECT count(compress_chunk(format('%I.%I', chunk_schema, chunk_name)::regclass, if_not_compressed => true)) " +
                "FROM timescaledb_information.chunks WHERE hypertable_name = 'messages' " +
                "AND range_start <= now() - interval '10 days' AND range_end > now() - interval '10 days'", conn);
            Assert.True((long)(await compress.ExecuteScalarAsync(Ct))! >= 1);
        }

        Assert.True(await OwnerCountAsync(
            "SELECT count(*) FROM timescaledb_information.chunks WHERE hypertable_name = 'messages' AND is_compressed " +
            "AND range_start <= now() - interval '10 days' AND range_end > now() - interval '10 days'") >= 1);

        var status = await EraseAsync(admin, user);
        await WaitCompletedAsync(admin, status.Id);

        Assert.Equal(3, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND sender_id = $2", room, Erased));
        var history = await ClientFor(factory, reader, "member").GetFromJsonAsync<RoomHistoryResponse>($"/rooms/{room}/messages", Ct);
        Assert.Equal(plain, history!.Messages.OrderBy(m => m.Seq).Select(m => m.Content).ToArray());
        Assert.All(history.Messages, m => Assert.Equal(Erased, m.SenderId));
    }

    [Fact]
    public async Task LiveEvent_Published()
    {
        var factory = NewFactory();
        var admin = await AdminAsync(factory);
        var room = Guid.NewGuid();
        var user = NewId("it-erw-user");
        await SeedAsync(user, "member", room);
        var seqs = await SendAsync(ClientFor(factory, user, "member"), room, "live", 3);
        await WaitPersistedAsync(room, 3);

        await using var connection = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:4222" });
        await using var sub = await connection.SubscribeCoreAsync<byte[]>($"room.{room}.ephemeral.erased", cancellationToken: Ct);

        var status = await EraseAsync(admin, user);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var message = await sub.Msgs.ReadAsync(cts.Token);
        var erased = JsonSerializer.Deserialize(message.Data!, DotwireJsonContext.Default.MessagesErased)!;

        Assert.Equal(room, erased.RoomId);
        Assert.Equal("anonymize", erased.Mode);
        Assert.Equal(seqs.ToHashSet(), erased.Seqs.ToHashSet());
        await WaitCompletedAsync(admin, status.Id);
    }

    private sealed class NoticeListener : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }

        public NoticeListener(Action<byte[]> onBody)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/erasure";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        using var ms = new MemoryStream();
                        await ctx.Request.InputStream.CopyToAsync(ms);
                        onBody(ms.ToArray());
                        ctx.Response.StatusCode = 200;
                        ctx.Response.Close();
                    }
                    catch
                    {
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch
            {
            }

            _listener.Close();
        }
    }
}
