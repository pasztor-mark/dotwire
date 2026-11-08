using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

public class ServerUnitSecurityTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void TokenRules_AcceptAWellFormedToken()
    {
        var principal = Principal(("sub", "u1"), ("dw:role", "member"), ("iat", "1800000000"), ("exp", "1800000900"));

        Assert.Null(TokenRules.Validate(principal, new AuthOptions(), Now));
    }

    [Theory]
    [InlineData("u\n1", "member", "1800000000", "1800000900")]
    [InlineData("u1", "root", "1800000000", "1800000900")]
    [InlineData("u1", "member", null, "1800000900")]
    [InlineData("u1", "member", "1800000100", "1800000900")]
    [InlineData("u1", "member", "1800000000", "1800007200")]
    public void TokenRules_RejectMalformedTokens(string sub, string role, string? iat, string exp)
    {
        var claims = new List<(string, string)> { ("sub", sub), ("dw:role", role), ("exp", exp) };
        if (iat is not null)
            claims.Add(("iat", iat));

        Assert.NotNull(TokenRules.Validate(Principal([.. claims]), new AuthOptions(), Now));
    }

    [Fact]
    public void TokenRules_LifetimeCapZeroDisablesTheCheck()
    {
        var principal = Principal(("sub", "u1"), ("dw:role", "member"), ("iat", "1800000000"), ("exp", "1900000000"));

        Assert.Null(TokenRules.Validate(principal, new AuthOptions { MaxTokenLifetimeMinutes = 0 }, Now));
    }

    [Theory]
    [InlineData("GET", "/rooms/0b6a3c7e-2f8d-4f5b-9a51-3a9d1c2e4f60/events", true)]
    [InlineData("GET", "/rooms/0b6a3c7e-2f8d-4f5b-9a51-3a9d1c2e4f60/events/", true)]
    [InlineData("GET", "/hub/rooms", true)]
    [InlineData("POST", "/hub/rooms/negotiate", false)]
    [InlineData("DELETE", "/admin/rooms/0b6a3c7e-2f8d-4f5b-9a51-3a9d1c2e4f60/members/events", false)]
    [InlineData("GET", "/admin/rooms/0b6a3c7e-2f8d-4f5b-9a51-3a9d1c2e4f60/members/events", false)]
    [InlineData("GET", "/rooms/not-a-guid/events", false)]
    [InlineData("POST", "/rooms/0b6a3c7e-2f8d-4f5b-9a51-3a9d1c2e4f60/events", false)]
    public void TokenRules_QueryTokenOnlyOnSseAndHub(string method, string path, bool expected)
    {
        Assert.Equal(expected, TokenRules.AcceptsQueryToken(method, new PathString(path)));
    }

    [Theory]
    [InlineData("https://hooks.example/presend", false, false)]
    [InlineData("http://localhost:9000/presend", false, false)]
    [InlineData("http://127.0.0.1:9000/presend", false, false)]
    [InlineData("http://hooks.example/presend", false, true)]
    [InlineData("http://hooks.example/presend", true, false)]
    [InlineData("file:///etc/passwd", true, true)]
    public void WebhookUrls_MustBeHttpsUnlessLoopbackOrOverridden(string url, bool allowInsecure, bool throws)
    {
        var options = new WebhooksOptions { AllowInsecureUrls = allowInsecure };
        options.PostSend.Url = url;

        var exception = Record.Exception(() => WebhookUrlRules.Validate(options));

        Assert.Equal(throws, exception is InvalidOperationException);
    }

    [Fact]
    public void WebhookSignature_CoversTimestampAndBody()
    {
        var body = "{\"a\":1}"u8.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://hooks.example");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_123);

        PresendWebhookClient.SignIfConfigured(request, body, "s3cret", now);

        var timestamp = request.Headers.GetValues(PresendWebhookClient.TimestampHeader).Single();
        var v2 = request.Headers.GetValues(PresendWebhookClient.SignatureV2Header).Single();
        var legacy = request.Headers.GetValues(PresendWebhookClient.SignatureHeader).Single();
        var key = Encoding.UTF8.GetBytes("s3cret");

        Assert.Equal("1800000123", timestamp);
        Assert.Equal($"sha256={Convert.ToHexStringLower(HMACSHA256.HashData(key, "1800000123.{\"a\":1}"u8.ToArray()))}", v2);
        Assert.Equal($"sha256={Convert.ToHexStringLower(HMACSHA256.HashData(key, body))}", legacy);
    }

    [Fact]
    public void WebhookSignature_IsOmittedWithoutSecret()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://hooks.example");

        PresendWebhookClient.SignIfConfigured(request, [1], null, DateTimeOffset.UtcNow);

        Assert.Empty(request.Headers);
    }

    [Fact]
    public void PostSendQueue_IsBoundedByBytes()
    {
        var options = new WebhooksOptions();
        options.PostSend.Url = "https://hooks.example/postsend";
        options.PostSend.MaxQueueBytes = 1000;
        var service = new PostSendWebhookService(new NoHttp(), Options.Create(options), NullLogger<PostSendWebhookService>.Instance);
        var request = new PostSendWebhookRequest(Guid.NewGuid(), 1, "u1", new string('x', 300), DateTimeOffset.UtcNow, "member");

        service.Enqueue(request);
        service.Enqueue(request);

        Assert.Equal(PostSendWebhookService.EstimateBytes(request), service.QueuedBytes);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    [Fact]
    public void PostgresWriter_RejectsMalformedPayloads()
    {
        var valid = JsonSerializer.SerializeToUtf8Bytes(
            new RoomMessage(Guid.NewGuid(), "u1", DateTimeOffset.UtcNow, "k1", [1, 2, 3]), DotwireJsonContext.Default.RoomMessage);
        var badSender = JsonSerializer.SerializeToUtf8Bytes(
            new RoomMessage(Guid.NewGuid(), "u\01", DateTimeOffset.UtcNow, "k1", [1]), DotwireJsonContext.Default.RoomMessage);

        Assert.NotNull(PostgresWriterService.TryParse(valid));
        Assert.Null(PostgresWriterService.TryParse("{not json"u8.ToArray()));
        Assert.Null(PostgresWriterService.TryParse("null"u8.ToArray()));
        Assert.Null(PostgresWriterService.TryParse([]));
        Assert.Null(PostgresWriterService.TryParse(null));
        Assert.Null(PostgresWriterService.TryParse(badSender));
    }

    [Theory]
    [InlineData("22P02", true)]
    [InlineData("23502", true)]
    [InlineData("08006", false)]
    [InlineData("53300", false)]
    [InlineData("40001", false)]
    public void PostgresWriter_OnlyDataErrorsArePermanent(string sqlState, bool permanent)
    {
        var ex = new PostgresException("boom", "ERROR", "ERROR", sqlState);

        Assert.Equal(permanent, PostgresWriterService.IsPermanent(ex));
    }

    [Fact]
    public void AuditWriter_MalformedEventsBecomePlaceholders()
    {
        var time = DateTimeOffset.UtcNow;
        var good = JsonSerializer.SerializeToUtf8Bytes(
            new AuditEvent("member.added", Guid.NewGuid(), null, "a1", "u1", null, time), DotwireJsonContext.Default.AuditEvent);
        var forged = JsonSerializer.SerializeToUtf8Bytes(
            new AuditEvent("member.added", null, null, "a\nb", null, null, time), DotwireJsonContext.Default.AuditEvent);

        Assert.Equal("member.added", AuditWriterService.ParseOrPlaceholder(good, time).EventType);
        Assert.Equal(AuditWriterService.MalformedEventType, AuditWriterService.ParseOrPlaceholder("{"u8.ToArray(), time).EventType);
        Assert.Equal(AuditWriterService.MalformedEventType, AuditWriterService.ParseOrPlaceholder("null"u8.ToArray(), time).EventType);
        Assert.Equal(AuditWriterService.MalformedEventType, AuditWriterService.ParseOrPlaceholder(null, time).EventType);
        Assert.Equal(AuditWriterService.MalformedEventType, AuditWriterService.ParseOrPlaceholder(forged, time).EventType);
    }

    [Fact]
    public void AuditVerify_AnchorMustMatchTheAuditRow()
    {
        var hash = RandomNumberGenerator.GetBytes(32);

        Assert.True(Audit.AnchorMatches(hash, (byte[])hash.Clone()));
        Assert.False(Audit.AnchorMatches(hash, null));
        Assert.False(Audit.AnchorMatches(hash, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Migrations_ModifiedAppliedScriptsAreDetected()
    {
        var scripts = new[] { ("0001_a.sql", "SELECT 1;"), ("0002_b.sql", "SELECT 2;") };
        var applied = new Dictionary<string, string?>
        {
            ["0001_a.sql"] = MigrationRunner.Checksum("SELECT 1;"),
            ["0002_b.sql"] = MigrationRunner.Checksum("SELECT 2; -- edited"),
        };

        Assert.Equal(["0002_b.sql"], MigrationRunner.ModifiedScripts(applied, scripts));
    }

    [Fact]
    public void Migrations_LegacyRowsWithoutChecksumAreNotModified()
    {
        var applied = new Dictionary<string, string?> { ["0001_a.sql"] = null };

        Assert.Empty(MigrationRunner.ModifiedScripts(applied, [("0001_a.sql", "SELECT 1;")]));
    }

    [Fact]
    public void Migrations_ChecksumIgnoresLineEndings()
    {
        Assert.Equal(MigrationRunner.Checksum("a\nb\n"), MigrationRunner.Checksum("a\r\nb\r\n"));
    }

    [Fact]
    public void Migrations_EmbeddedScriptsMatchTheirCommittedChecksums()
    {
        var scripts = MigrationRunner.LoadScripts().ToList();
        var applied = scripts.ToDictionary(s => s.Name, s => (string?)MigrationRunner.Checksum(s.Sql));

        Assert.Empty(MigrationRunner.ModifiedScripts(applied, scripts));
    }

    [Fact]
    public void Migration0009_HardensTheAuditChain()
    {
        var script = MigrationRunner.LoadScripts().Single(s => s.Name.StartsWith("0009_")).Sql;

        Assert.Matches(new Regex(@"CREATE\s+UNIQUE\s+INDEX\s+\w+\s+ON\s+audit_log\s*\(\s*prev_hash\s*\)", RegexOptions.IgnoreCase), script);
        Assert.Matches(new Regex(@"BEFORE\s+INSERT\s+ON\s+audit_checkpoints", RegexOptions.IgnoreCase), script);
        Assert.Matches(new Regex(@"ALTER\s+TABLE\s+audit_log\s+ADD\s+COLUMN\s+stream_time\s+timestamptz", RegexOptions.IgnoreCase), script);
        Assert.Matches(new Regex(@"NEW\.day\s*>", RegexOptions.IgnoreCase), script);
        Assert.Matches(new Regex(@"FROM\s+audit_log\s+WHERE\s+id\s*=\s*NEW\.last_id\s+AND\s+hash\s*=\s*NEW\.hash", RegexOptions.IgnoreCase), script);
        Assert.DoesNotMatch(new Regex(@"\b(UPDATE|DELETE)\s+(FROM\s+)?audit_", RegexOptions.IgnoreCase), script);
    }

    [Fact]
    public async Task Revocation_RetriesUntilDelivered()
    {
        var calls = 0;
        var failures = new List<int>();

        var delivered = await Admin.PublishWithRetryAsync(
            _ => ++calls < 3 ? throw new InvalidOperationException("nats down") : ValueTask.CompletedTask,
            3, TimeSpan.FromSeconds(1), (attempt, _) => failures.Add(attempt));

        Assert.True(delivered);
        Assert.Equal(3, calls);
        Assert.Equal([1, 2], failures);
    }

    [Fact]
    public async Task Revocation_ReportsFailureAfterAllAttempts()
    {
        var calls = 0;

        var delivered = await Admin.PublishWithRetryAsync(
            _ => { calls++; throw new InvalidOperationException("nats down"); }, 3, TimeSpan.FromSeconds(1));

        Assert.False(delivered);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Revocation_AttemptsAreTimeBoxed()
    {
        var delivered = await Admin.PublishWithRetryAsync(
            async ct => await Task.Delay(Timeout.Infinite, ct), 2, TimeSpan.FromMilliseconds(50));

        Assert.False(delivered);
    }

    [Theory]
    [InlineData(101, true, 202)]
    [InlineData(101, false, 101)]
    public void HistoryTail_ReadBudgetIsBounded(int limit, bool keepNewest, int expected)
    {
        Assert.Equal(expected, Messages.TailReadBudget(limit, keepNewest));
    }

    [Fact]
    public void Presend_RewriteIsRevalidated()
    {
        var (error, content) = Messages.ApplyPresend(new PresendVerdict(PresendOutcome.AllowedWithRewrite, Content: new string('x', 20)), "orig", 10);
        var (okError, okContent) = Messages.ApplyPresend(new PresendVerdict(PresendOutcome.AllowedWithRewrite, Content: "short"), "orig", 10);

        Assert.NotNull(error);
        Assert.Equal("orig", content);
        Assert.Null(okError);
        Assert.Equal("short", okContent);
    }
}
