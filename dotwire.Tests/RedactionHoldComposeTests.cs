using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public sealed class RedactionHoldComposeTests : IAsyncDisposable
{
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;
    private readonly string _appPassword;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly List<long> _placed = [];
    private readonly List<string> _streams = [];

    public RedactionHoldComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        _appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={_appPassword}";
        _factory = NewFactory();
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

    private WebApplicationFactory<Program> NewFactory(Action<IWebHostBuilder>? configure = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
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
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseHoldsAsync(_placed.ToArray());
        foreach (var factory in _factories)
            await factory.DisposeAsync();
        foreach (var stream in _streams)
            await DeleteStreamQuietlyAsync(stream);
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

    private HttpClient ClientFor(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient AdminClient(WebApplicationFactory<Program>? factory = null) =>
        ClientFor(factory ?? _factory, TestTokens.Mint("host-admin", role: "admin"));

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

    private async Task ReleaseHoldsAsync(params long[] ids)
    {
        if (ids.Length == 0)
            return;

        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(CancellationToken.None);
        await using (var release = new NpgsqlCommand(
            "INSERT INTO legal_hold_releases (hold_id, released_by) " +
            "SELECT h.id, 'test-cleanup' FROM unnest($1::bigint[]) AS h(id) " +
            "WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)", conn))
        {
            release.Parameters.AddWithValue(ids);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var sync = new NpgsqlCommand("SELECT sync_retention_schedule()", conn);
        await sync.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task PlaceHoldAsync(object body)
    {
        var response = await AdminClient().PostAsJsonAsync("/admin/holds", body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = await response.Content.ReadFromJsonAsync<LegalHold>(Ct);
        _placed.Add(hold!.Id);
    }

    private Task PlaceRoomHoldAsync(Guid room) => PlaceHoldAsync(new { roomId = room, matterRef = "matter-" + Guid.NewGuid() });

    private Task PlaceUserHoldAsync(string user) => PlaceHoldAsync(new { userId = user, matterRef = "matter-" + Guid.NewGuid() });

    private async Task<(Guid Room, string Member, ulong Seq)> SendAsync(WebApplicationFactory<Program>? factory = null)
    {
        var room = Guid.NewGuid();
        var member = $"it-red-{Guid.NewGuid():N}";
        await SeedAsync(member, "member", room);
        var send = await ClientFor(factory ?? _factory, TestTokens.Mint(member))
            .PostAsJsonAsync($"/rooms/{room}/messages", new { content = "redaction subject" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var ack = await send.Content.ReadFromJsonAsync<SendMessageResponse>(Ct);
        return (room, member, ack!.Seq);
    }

    private async Task<RoomHistoryResponse> HistoryAsync(Guid room, string member, WebApplicationFactory<Program>? factory = null) =>
        (await ClientFor(factory ?? _factory, TestTokens.Mint(member)).GetFromJsonAsync<RoomHistoryResponse>($"/rooms/{room}/messages", Ct))!;

    private async Task<bool> StreamHasAsync(ulong seq)
    {
        var js = _factory.Services.GetRequiredService<INatsJSContext>();
        var stream = await js.GetStreamAsync(_factory.Services.GetRequiredService<IOptions<NatsOptions>>().Value.RoomsStream, cancellationToken: Ct);
        try
        {
            await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, Ct);
            return true;
        }
        catch (NatsJSApiException)
        {
            return false;
        }
    }

    private async Task AssertNoRedactAuditAsync(Guid room, ulong seq)
    {
        await Task.Delay(1500, Ct);
        Assert.Equal(0, await OwnerCountAsync(
            "SELECT count(*) FROM audit_log WHERE event_type = 'message.redacted' AND room_id = $1 AND message_seq = $2", room, (long)seq));
    }

    private async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct);
        Assert.Equal(code, body?.Error);
    }

    [Fact]
    public async Task Redact_HeldRoom_Is409_AndNoAuditRow()
    {
        var (room, member, seq) = await SendAsync();
        await PlaceRoomHoldAsync(room);

        var response = await AdminClient().DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);

        await AssertConflictAsync(response, "legal_hold");
        await AssertNoRedactAuditAsync(room, seq);
        Assert.Contains((await HistoryAsync(room, member)).Messages, m => m.Seq == seq);
        Assert.True(await StreamHasAsync(seq));
    }

    [Fact]
    public async Task Redact_UserHold_NotYetPersisted_Is409()
    {
        var (room, member, seq) = await SendAsync();
        await PlaceUserHoldAsync(member);

        var response = await AdminClient().DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);

        await AssertConflictAsync(response, "legal_hold");
        await AssertNoRedactAuditAsync(room, seq);
        Assert.True(await StreamHasAsync(seq));
    }

    [Fact]
    public async Task Redact_MinimumKeep_Is409()
    {
        var strict = NewFactory(b => b.UseSetting("Dotwire:Compliance:MinimumKeepDays", "1"));
        _ = strict.Services;
        var (room, member, seq) = await SendAsync(strict);

        var response = await AdminClient(strict).DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);

        await AssertConflictAsync(response, "minimum_keep");
        await AssertNoRedactAuditAsync(room, seq);
        Assert.Contains((await HistoryAsync(room, member, strict)).Messages, m => m.Seq == seq);
    }

    [Fact]
    public async Task Redact_ChangesNotAllowed_Is409()
    {
        var writeOnce = NewFactory(b => b.UseSetting("Dotwire:Compliance:AllowChanges", "false"));
        _ = writeOnce.Services;
        var (room, member, seq) = await SendAsync(writeOnce);

        var response = await AdminClient(writeOnce).DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);

        await AssertConflictAsync(response, "changes_not_allowed");
        await AssertNoRedactAuditAsync(room, seq);
        Assert.Contains((await HistoryAsync(room, member, writeOnce)).Messages, m => m.Seq == seq);
    }

    [Fact]
    public async Task Redact_NoHold_StillWorks_AndPublishesNotice()
    {
        var stream = $"ERASURES_T{Guid.NewGuid():N}";
        var id = stream["ERASURES_T".Length..];
        _streams.Add(stream);
        var received = new TaskCompletionSource<ErasureNotice>();
        using var listener = StartListener(body =>
        {
            var notice = JsonSerializer.Deserialize(body, DotwireJsonContext.Default.ErasureNotice);
            received.TrySetResult(notice!);
        }, out var url);
        var noticed = NewFactory(b =>
        {
            b.UseSetting("Nats:ErasureNoticesStream", stream);
            b.UseSetting("Nats:ErasureNoticesSubject", $"dotwire.erasures.t{id}");
            b.UseSetting("Nats:ErasureNoticesConsumer", $"erasure-notifier-t{id}");
            b.UseSetting("Dotwire:Webhooks:Erasure:Url", url);
        });
        _ = noticed.Services;
        var (room, member, seq) = await SendAsync(noticed);

        var response = await AdminClient(noticed).DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15), Ct));
        Assert.Same(received.Task, completed);
        var notice = await received.Task;
        Assert.Equal("message.redacted", notice.Event);
        Assert.Equal(room, notice.RoomId);
        Assert.Equal(seq, notice.Seq);
        Assert.DoesNotContain((await HistoryAsync(room, member, noticed)).Messages, m => m.Seq == seq);
    }

    [Fact]
    public async Task Emergency_HeldRoom_Removes_AndAuditsReasonCode()
    {
        var (room, member, seq) = await SendAsync();
        await PlaceRoomHoldAsync(room);

        var response = await AdminClient().PostAsJsonAsync(
            $"/admin/rooms/{room}/messages/{seq}/emergency-delete", new { reason = "court_order" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RedactMessageResponse>(Ct);
        Assert.True(body!.RemovedFromStream);
        Assert.DoesNotContain((await HistoryAsync(room, member)).Messages, m => m.Seq == seq);
        Assert.False(await StreamHasAsync(seq));

        long value = -1;
        for (var i = 0; i < 40 && value != 2; i++)
        {
            value = await OwnerCountAsync(
                "SELECT coalesce(max(value), -1) FROM audit_log WHERE event_type = 'message.redacted.emergency' AND room_id = $1 AND message_seq = $2",
                room, (long)seq);
            if (value != 2)
                await Task.Delay(250, Ct);
        }
        Assert.Equal(2, value);
    }

    [Fact]
    public async Task Emergency_MissingReason_Is400()
    {
        var (room, _, seq) = await SendAsync();

        var response = await AdminClient().PostAsJsonAsync($"/admin/rooms/{room}/messages/{seq}/emergency-delete", new { }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
        Assert.True(await StreamHasAsync(seq));
    }

    [Fact]
    public async Task Emergency_UnknownReason_Is400()
    {
        var (room, _, seq) = await SendAsync();

        var response = await AdminClient().PostAsJsonAsync(
            $"/admin/rooms/{room}/messages/{seq}/emergency-delete", new { reason = "because" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
        Assert.True(await StreamHasAsync(seq));
    }

    [Fact]
    public async Task InjectionCleanup_StillErases_InHeldRoom()
    {
        var room = Guid.NewGuid();
        await PlaceRoomHoldAsync(room);
        var failing = NewFactory(b => b.ConfigureTestServices(s => s.AddSingleton<AuditPublisher>(new ThrowingAuditPublisher())));
        var options = failing.Services.GetRequiredService<IOptions<NatsOptions>>().Value;
        var js = failing.Services.GetRequiredService<INatsJSContext>();

        var response = await AdminClient(failing).PostAsJsonAsync($"/admin/rooms/{room}/messages", new { content = "unaudited" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var stream = await js.GetStreamAsync(options.RoomsStream, cancellationToken: Ct);
        var exception = await Record.ExceptionAsync(() =>
            stream.GetAsync(new StreamMsgGetRequest { LastBySubj = $"{options.RoomSubjectPrefix}{room}" }, Ct).AsTask());
        Assert.IsType<NatsJSApiException>(exception);
        Assert.Equal(1, await OwnerCountAsync("SELECT count(*) FROM message_redactions WHERE room_id = $1", room));
    }

    private async Task WaitForRowAsync(Guid room, ulong seq)
    {
        for (var i = 0; i < 40; i++)
        {
            if (await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND seq = $2", room, (long)seq) == 1)
                return;
            await Task.Delay(250, Ct);
        }
        Assert.Fail("message row never appeared in Postgres within 10s");
    }

    private async Task AssertUntouchedAsync(Guid room, ulong seq)
    {
        Assert.Equal(1, await OwnerCountAsync("SELECT count(*) FROM messages WHERE room_id = $1 AND seq = $2", room, (long)seq));
        Assert.Equal(0, await OwnerCountAsync("SELECT count(*) FROM message_redactions WHERE room_id = $1 AND seq = $2", room, (long)seq));
        Assert.True(await StreamHasAsync(seq));
    }

    [Fact]
    public async Task Redact_AuditFailure_Is503_AndRowStillExists()
    {
        var (room, _, seq) = await SendAsync();
        await WaitForRowAsync(room, seq);
        var failing = NewFactory(b => b.ConfigureTestServices(s => s.AddSingleton<AuditPublisher>(new ThrowingAuditPublisher())));

        var response = await AdminClient(failing).DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("audit_unavailable", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
        await AssertUntouchedAsync(room, seq);
    }

    [Fact]
    public async Task Emergency_AuditFailure_Is503_AndRowStillExists()
    {
        var (room, _, seq) = await SendAsync();
        await WaitForRowAsync(room, seq);
        var failing = NewFactory(b => b.ConfigureTestServices(s => s.AddSingleton<AuditPublisher>(new ThrowingAuditPublisher())));

        var response = await AdminClient(failing).PostAsJsonAsync(
            $"/admin/rooms/{room}/messages/{seq}/emergency-delete", new { reason = "illegal_content" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("audit_unavailable", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
        await AssertUntouchedAsync(room, seq);
    }

    [Fact]
    public async Task Redact_NoticeFailure_Is503_ThenRetrySucceedsAndPublishes()
    {
        var stream = $"ERASURES_T{Guid.NewGuid():N}";
        var id = stream["ERASURES_T".Length..];
        _streams.Add(stream);
        var notices = new List<ErasureNotice>();
        var noticed = NewFactory(b =>
        {
            b.UseSetting("Nats:ErasureNoticesStream", stream);
            b.UseSetting("Nats:ErasureNoticesSubject", $"dotwire.erasures.t{id}");
            b.UseSetting("Nats:ErasureNoticesConsumer", $"erasure-notifier-t{id}");
            b.UseSetting("Dotwire:Webhooks:Erasure:Url", "http://127.0.0.1:9/webhook");
            b.ConfigureTestServices(s => s.AddSingleton<ErasureNoticePublisher>(sp => new FailOnceNoticePublisher(
                sp.GetRequiredService<INatsJSContext>(),
                sp.GetRequiredService<IOptions<NatsOptions>>(),
                sp.GetRequiredService<IOptions<WebhooksOptions>>(),
                notices)));
        });
        _ = noticed.Services;
        var (room, member, seq) = await SendAsync(noticed);
        await WaitForRowAsync(room, seq);

        var first = await AdminClient(noticed).DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal("notice_unavailable", (await first.Content.ReadFromJsonAsync<ApiErrorResponse>(Ct))?.Error);
        Assert.Empty(notices);
        Assert.DoesNotContain((await HistoryAsync(room, member, noticed)).Messages, m => m.Seq == seq);

        var second = await AdminClient(noticed).DeleteAsync($"/admin/rooms/{room}/messages/{seq}", Ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var notice = Assert.Single(notices);
        Assert.Equal("message.redacted", notice.Event);
        Assert.Equal(room, notice.RoomId);
        Assert.Equal(seq, notice.Seq);
        Assert.DoesNotContain((await HistoryAsync(room, member, noticed)).Messages, m => m.Seq == seq);
        Assert.False(await StreamHasAsync(seq));
    }

    private const string HoldLockKeySql = "hashtextextended('dotwire.legal_hold', 0)";

    private async Task AssertBlockedOnHoldLockAsync(Task pending)
    {
        var sql = $"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted " +
                  $"AND classid::bigint = ({HoldLockKeySql} >> 32) & 4294967295 AND objid::bigint = {HoldLockKeySql} & 4294967295";
        for (var i = 0; i < 40; i++)
        {
            if (await OwnerCountAsync(sql) > 0)
            {
                Assert.False(pending.IsCompleted);
                return;
            }
            await Task.Delay(100, Ct);
        }
        Assert.Fail("request never waited on the legal hold advisory lock");
    }

    [Fact]
    public async Task Redact_BlocksBehindExclusiveHoldLock_ThenSeesHold_Is409()
    {
        var (room, _, seq) = await SendAsync();
        await WaitForRowAsync(room, seq);

        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        var tx = await conn.BeginTransactionAsync(Ct);
        var committed = false;
        long holdId = 0;
        try
        {
            await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({HoldLockKeySql})", conn, tx))
                await lockCmd.ExecuteNonQueryAsync(Ct);
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO legal_holds (scope, room_id, matter_ref, placed_by) VALUES ('room', $1, 'race', 'test') RETURNING id", conn, tx))
            {
                insert.Parameters.AddWithValue(room);
                holdId = (long)(await insert.ExecuteScalarAsync(Ct))!;
            }

            var admin = AdminClient();
            var pending = Task.Run(() => admin.DeleteAsync($"/admin/rooms/{room}/messages/{seq}", CancellationToken.None));
            await AssertBlockedOnHoldLockAsync(pending);

            await tx.CommitAsync(Ct);
            committed = true;
            _placed.Add(holdId);

            var response = await pending.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            await AssertConflictAsync(response, "legal_hold");
            await AssertUntouchedAsync(room, seq);
            await AssertNoRedactAuditAsync(room, seq);
        }
        finally
        {
            if (!committed)
                await tx.RollbackAsync(CancellationToken.None);
            await tx.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlaceHold_BlocksBehindSharedHoldLock()
    {
        var room = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_ownerConnectionString);
        await conn.OpenAsync(Ct);
        var tx = await conn.BeginTransactionAsync(Ct);
        var committed = false;
        try
        {
            await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock_shared({HoldLockKeySql})", conn, tx))
                await lockCmd.ExecuteNonQueryAsync(Ct);

            var admin = AdminClient();
            var pending = Task.Run(() => admin.PostAsJsonAsync(
                "/admin/holds", new { roomId = room, matterRef = "matter-" + Guid.NewGuid() }, CancellationToken.None));
            await AssertBlockedOnHoldLockAsync(pending);

            await tx.CommitAsync(Ct);
            committed = true;

            var response = await pending.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var hold = await response.Content.ReadFromJsonAsync<LegalHold>(Ct);
            _placed.Add(hold!.Id);
        }
        finally
        {
            if (!committed)
                await tx.RollbackAsync(CancellationToken.None);
            await tx.DisposeAsync();
        }
    }

    private sealed class FailOnceNoticePublisher(
        INatsJSContext js, IOptions<NatsOptions> nats, IOptions<WebhooksOptions> webhooks, List<ErasureNotice> sink)
        : ErasureNoticePublisher(js, nats, webhooks)
    {
        private int _calls;

        public override Task PublishAsync(ErasureNotice notice, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("notice publish failed");
            sink.Add(notice);
            return Task.CompletedTask;
        }
    }

    private static IDisposable StartListener(Action<byte[]> onBody, out string url)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        url = $"http://127.0.0.1:{port}/webhook";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                onBody(ms.ToArray());
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        });
        return new ListenerHandle(listener);
    }

    private sealed class ListenerHandle(HttpListener listener) : IDisposable
    {
        public void Dispose() => listener.Close();
    }
}
