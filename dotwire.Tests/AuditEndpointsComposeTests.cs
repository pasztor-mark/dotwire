using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dotwire.Api;
using Dotwire.Nats;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Full-pipeline tests for `/audit`, `/audit/verify`, `/audit/checkpoints` (spec §3.7, §10.2)
/// against compose-provided Postgres + NATS.
/// </summary>
[Collection("Compose")]
public class AuditEndpointsComposeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string PgConnectionString =
        "Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password=";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _pgPassword;

    public AuditEndpointsComposeTests(WebApplicationFactory<Program> factory)
    {
        SkipUnlessReachable("localhost", 5432, "Postgres");
        SkipUnlessReachable("localhost", 4222, "NATS");

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

    private async Task SeedUserAsync(string sub, string role)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO user_roles (user_id, role) VALUES ($1, $2)
            ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role
            """, conn);
        cmd.Parameters.AddWithValue(sub);
        cmd.Parameters.AddWithValue(role);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<long> CountAuditReadRowsAsync(string actorId)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE event_type = 'audit.read' AND actor_id = $1", conn);
        cmd.Parameters.AddWithValue(actorId);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> WaitForAuditReadRowAsync(string actorId)
    {
        for (var i = 0; i < 80; i++)
        {
            var count = await CountAuditReadRowsAsync(actorId);
            if (count >= 1)
                return count;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"expected an audit.read row for actor {actorId} within 20s");
        return 0;
    }

    [Fact]
    public async Task Auditor_CanReadPaginatedLog_AndEmitsAuditReadEvent()
    {
        var actorId = $"it-auditor-{Guid.NewGuid():N}";
        await SeedUserAsync(actorId, "auditor");
        var client = Client(TestTokens.Mint(actorId, role: "auditor"));

        // Seed a few rows directly through the publisher so we don't depend on other
        // endpoints being wired to it yet.
        var publisher = _factory.Services.GetRequiredService<AuditPublisher>();
        var subjectActor = $"it-subject-{Guid.NewGuid():N}";
        for (var i = 0; i < 3; i++)
        {
            await publisher.PublishAsync(
                new AuditEvent("role.set.member", null, null, subjectActor, $"user-{i}", null, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }
        await WaitForRowCountAsync(subjectActor, 3);

        var response = await client.GetAsync("/audit?limit=2", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<AuditPageResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(2, page.Events.Length);
        Assert.True(page.Events[0].Id < page.Events[1].Id);

        await WaitForAuditReadRowAsync(actorId);
    }

    [Fact]
    public async Task Auditor_CanReadCheckpoints()
    {
        var actorId = $"it-auditor-{Guid.NewGuid():N}";
        await SeedUserAsync(actorId, "auditor");
        var client = Client(TestTokens.Mint(actorId, role: "auditor"));

        // Force at least one checkpoint by publishing a row and letting the writer commit it.
        var publisher = _factory.Services.GetRequiredService<AuditPublisher>();
        var subjectActor = $"it-checkpoint-{Guid.NewGuid():N}";
        await publisher.PublishAsync(
            new AuditEvent("role.set.member", null, null, subjectActor, "user-x", null, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        await WaitForRowCountAsync(subjectActor, 1);

        var response = await client.GetAsync("/audit/checkpoints?limit=5", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditCheckpointsResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotEmpty(body.Checkpoints);
        // newest first
        for (var i = 1; i < body.Checkpoints.Length; i++)
            Assert.True(body.Checkpoints[i - 1].Day >= body.Checkpoints[i].Day);
    }

    [Fact]
    public async Task Verify_ValidChain_IsOk_TamperedRow_ReportsFirstInvalidId()
    {
        var actorId = $"it-auditor-{Guid.NewGuid():N}";
        await SeedUserAsync(actorId, "auditor");
        var client = Client(TestTokens.Mint(actorId, role: "auditor"));

        var publisher = _factory.Services.GetRequiredService<AuditPublisher>();
        var subjectActor = $"it-verify-{Guid.NewGuid():N}";
        for (var i = 0; i < 3; i++)
        {
            await publisher.PublishAsync(
                new AuditEvent("role.set.member", null, null, subjectActor, $"user-{i}", null, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }
        var rows = await WaitForRowCountAsync(subjectActor, 3);

        var okResponse = await client.GetAsync("/audit/verify?full=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
        var okBody = await okResponse.Content.ReadFromJsonAsync<AuditVerificationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(okBody);
        Assert.True(okBody.Ok);
        Assert.Null(okBody.FirstInvalidId);

        // Tamper the middle row's subject_id (as owner, bypassing the append-only trigger),
        // then restore the original value once we're done: this Postgres instance is a
        // persistent compose service shared across test runs, and `verify?full=true` walks
        // the whole table from genesis - a corruption left behind here would permanently
        // break every future full-chain verification, in this suite and any other.
        var tamperedId = rows[1];
        const string originalSubjectId = "user-1"; // rows[1] is the second publish in the loop above (i = 1).
        await using (var conn = await OpenOwnerConnectionAsync())
        {
            await using (var disable = new NpgsqlCommand("ALTER TABLE audit_log DISABLE TRIGGER audit_log_append_only", conn))
                await disable.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            try
            {
                await using (var update = new NpgsqlCommand(
                    "UPDATE audit_log SET subject_id = 'tampered' WHERE id = $1", conn))
                {
                    update.Parameters.AddWithValue(tamperedId);
                    await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }

                var tamperedResponse = await client.GetAsync("/audit/verify?full=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, tamperedResponse.StatusCode);
                var tamperedBody = await tamperedResponse.Content.ReadFromJsonAsync<AuditVerificationResponse>(TestContext.Current.CancellationToken);
                Assert.NotNull(tamperedBody);
                Assert.False(tamperedBody.Ok);
                Assert.Equal(tamperedId, tamperedBody.FirstInvalidId);
            }
            finally
            {
                await using (var restore = new NpgsqlCommand(
                    "UPDATE audit_log SET subject_id = $2 WHERE id = $1", conn))
                {
                    restore.Parameters.AddWithValue(tamperedId);
                    restore.Parameters.AddWithValue(originalSubjectId);
                    await restore.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }

                await using var enable = new NpgsqlCommand("ALTER TABLE audit_log ENABLE TRIGGER audit_log_append_only", conn);
                await enable.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        var restoredResponse = await client.GetAsync("/audit/verify?full=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
        var restoredBody = await restoredResponse.Content.ReadFromJsonAsync<AuditVerificationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(restoredBody);
        Assert.True(restoredBody.Ok);
    }

    private async Task<List<long>> WaitForRowCountAsync(string subjectActor, int expectedCount)
    {
        for (var i = 0; i < 80; i++)
        {
            var ids = await LoadIdsAsync(subjectActor);
            if (ids.Count >= expectedCount)
                return ids;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"expected {expectedCount} audit_log rows for subject {subjectActor} within 20s");
        return [];
    }

    private async Task<List<long>> LoadIdsAsync(string subjectActor)
    {
        await using var conn = await OpenOwnerConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM audit_log WHERE actor_id = $1 ORDER BY id", conn);
        cmd.Parameters.AddWithValue(subjectActor);
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            ids.Add(reader.GetInt64(0));
        return ids;
    }
}
