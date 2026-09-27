using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dotwire.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Presend/postsend webhook tests (spec §3.8) against compose-provided Postgres + NATS -
/// a real send needs a real JetStream ack, so these aren't in-process-only. Each test builds
/// its own factory (not a shared fixture) since each configures a different webhook endpoint.
/// </summary>
[Collection("Compose")]
public class WebhookComposeTests
{
    private const string PgConnectionString =
        "Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password=";

    static WebhookComposeTests() => DotNetEnv.Env.TraversePath().Load();

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

    private WebApplicationFactory<Program> BuildFactory(Action<IWebHostBuilderSettings> configureWebhooks)
    {
        SkipUnlessReachable("localhost", 5432, "Postgres");
        SkipUnlessReachable("localhost", 4222, "NATS");

        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresMigrator",
                $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}");
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
            configureWebhooks(new WebHostBuilderSettingsAdapter(b));
        });
        _ = factory.Services;
        return factory;
    }

    private interface IWebHostBuilderSettings
    {
        void UseSetting(string key, string value);
    }

    private sealed class WebHostBuilderSettingsAdapter(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) : IWebHostBuilderSettings
    {
        public void UseSetting(string key, string value) => builder.UseSetting(key, value);
    }

    private async Task<NpgsqlConnectionScope> SeedMemberAsync(string sub, Guid roomId)
    {
        var conn = new Npgsql.NpgsqlConnection(PgConnectionString + Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role) VALUES ($1, 'member') ON CONFLICT (user_id) DO UPDATE SET role = 'member'", conn))
        {
            cmd.Parameters.AddWithValue(sub);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn))
        {
            cmd.Parameters.AddWithValue(roomId);
            cmd.Parameters.AddWithValue(sub);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        return new NpgsqlConnectionScope(conn);
    }

    private sealed class NpgsqlConnectionScope(Npgsql.NpgsqlConnection connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    [Fact]
    public async Task Presend_Allow_PublishesUnmodified()
    {
        await using var stub = new StubWebhookServer(_ => (200, """{"allow":true}"""));
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url));

        var roomId = Guid.NewGuid();
        var sub = $"wh-allow-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hello" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Presend_Rewrite_PublishesRewrittenContent()
    {
        await using var stub = new StubWebhookServer(_ => (200, """{"allow":true,"content":"REDACTED"}"""));
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url));
        var roomId = Guid.NewGuid();
        var sub = $"wh-rewrite-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "bad word" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Presend_RewriteOversized_IsInvalidContent()
    {
        var oversized = new string('x', 70_000);
        await using var stub = new StubWebhookServer(_ => (200, $$"""{"allow":true,"content":"{{oversized}}"}"""));
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url));
        var roomId = Guid.NewGuid();
        var sub = $"wh-oversized-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_content", body!.Error);
    }

    [Fact]
    public async Task Presend_Reject_Is422WithReason()
    {
        await using var stub = new StubWebhookServer(_ => (200, """{"allow":false,"reason":"blocked"}"""));
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url));
        var roomId = Guid.NewGuid();
        var sub = $"wh-reject-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("presend_rejected", body!.Error);
        Assert.Equal("blocked", body.Reason);
    }

    [Fact]
    public async Task Presend_TimeoutUnderClosed_Is503()
    {
        await using var stub = new StubWebhookServer(_ => { Thread.Sleep(1500); return (200, """{"allow":true}"""); });
        await using var factory = BuildFactory(s =>
        {
            s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url);
            s.UseSetting("Dotwire:Webhooks:Presend:TimeoutMs", "200");
            s.UseSetting("Dotwire:Webhooks:Presend:FailPolicy", "Closed");
        });
        var roomId = Guid.NewGuid();
        var sub = $"wh-timeout-closed-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("presend_unavailable", body!.Error);
    }

    [Fact]
    public async Task Presend_TimeoutUnderOpen_PublishesOriginalContent()
    {
        await using var stub = new StubWebhookServer(_ => { Thread.Sleep(1500); return (200, """{"allow":true}"""); });
        await using var factory = BuildFactory(s =>
        {
            s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url);
            s.UseSetting("Dotwire:Webhooks:Presend:TimeoutMs", "200");
            s.UseSetting("Dotwire:Webhooks:Presend:FailPolicy", "Open");
        });
        var roomId = Guid.NewGuid();
        var sub = $"wh-timeout-open-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Presend_BadJson_FollowsFailPolicy()
    {
        await using var stub = new StubWebhookServer(_ => (200, "not json"));
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url));
        var roomId = Guid.NewGuid();
        var sub = $"wh-badjson-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // default FailPolicy = Closed
    }

    [Fact]
    public async Task Presend_Signature_IsSentAndValid()
    {
        const string secret = "test-secret";
        string? capturedSignature = null;
        byte[]? capturedBody = null;
        await using var stub = new StubWebhookServer(req =>
        {
            capturedSignature = req.Headers["X-Dotwire-Signature"];
            using var ms = new MemoryStream();
            req.InputStream.CopyTo(ms);
            capturedBody = ms.ToArray();
            return (200, """{"allow":true}""");
        });
        await using var factory = BuildFactory(s =>
        {
            s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url);
            s.UseSetting("Dotwire:Webhooks:Secret", secret);
        });
        var roomId = Guid.NewGuid();
        var sub = $"wh-sig-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.NotNull(capturedSignature);
        Assert.StartsWith("sha256=", capturedSignature);
        var expectedHash = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), capturedBody!));
        Assert.Equal($"sha256={expectedHash}", capturedSignature);
    }

    [Fact]
    public async Task AdminInjection_SkipsWebhooksByDefault()
    {
        var calls = 0;
        await using var stub = new StubWebhookServer(_ => { Interlocked.Increment(ref calls); return (200, """{"allow":true}"""); });
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:Presend:Url", stub.Url));
        var roomId = Guid.NewGuid();
        var sub = $"wh-member-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var admin = $"wh-admin-{Guid.NewGuid():N}";
        await using var conn = new Npgsql.NpgsqlConnection(PgConnectionString + Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role) VALUES ($1, 'admin') ON CONFLICT (user_id) DO UPDATE SET role = 'admin'", conn))
        {
            cmd.Parameters.AddWithValue(admin);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(admin, role: "admin"));
        var response = await client.PostAsJsonAsync($"/admin/rooms/{roomId}/messages", new { content = "system says hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, calls); // IncludeAdminSends defaults to false
    }

    [Fact]
    public async Task PostSend_Delivers()
    {
        var received = new TaskCompletionSource<PostSendWebhookRequest>();
        await using var stub = new StubWebhookServer(req =>
        {
            using var ms = new MemoryStream();
            req.InputStream.CopyTo(ms);
            var payload = System.Text.Json.JsonSerializer.Deserialize(ms.ToArray(), DotwireJsonContext.Default.PostSendWebhookRequest);
            if (payload is not null)
                received.TrySetResult(payload);
            return (200, "{}");
        });
        await using var factory = BuildFactory(s => s.UseSetting("Dotwire:Webhooks:PostSend:Url", stub.Url));
        var roomId = Guid.NewGuid();
        var sub = $"wh-postsend-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hello postsend" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(received.Task, completed);
        var payload = await received.Task;
        Assert.Equal(roomId, payload.RoomId);
        Assert.Equal(sub, payload.SenderId);
    }

    [Fact]
    public async Task PostSend_RetriesThenDropsAfterMaxAttempts()
    {
        var attempts = 0;
        await using var stub = new StubWebhookServer(_ => { Interlocked.Increment(ref attempts); return (500, "{}"); });
        await using var factory = BuildFactory(s =>
        {
            s.UseSetting("Dotwire:Webhooks:PostSend:Url", stub.Url);
            s.UseSetting("Dotwire:Webhooks:PostSend:MaxAttempts", "2");
        });
        var roomId = Guid.NewGuid();
        var sub = $"wh-postsend-retry-{Guid.NewGuid():N}";
        await using var _ = await SeedMemberAsync(sub, roomId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint(sub));
        var response = await client.PostAsJsonAsync($"/rooms/{roomId}/messages", new { content = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Two attempts with a 1s backoff between: give it up to 5s to settle, then confirm no more come in.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
    }

    private sealed class StubWebhookServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerRequest, (int Status, string Body)> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }

        public StubWebhookServer(Func<HttpListenerRequest, (int Status, string Body)> handler)
        {
            _handler = handler;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/webhook";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
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

                _ = Task.Run(() =>
                {
                    try
                    {
                        var (status, body) = _handler(ctx.Request);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        var bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes);
                        ctx.Response.Close();
                    }
                    catch
                    {
                    }
                });
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
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
