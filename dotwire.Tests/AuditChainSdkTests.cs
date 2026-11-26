using System.Net;
using System.Text;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Host;
using Dotwire.Nats;
using AuditEntry = Dotwire.Host.AuditEntry;
using Xunit;

namespace dotwire.Tests;

public class AuditChainSdkTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Guid RoomId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void Fixture_VerifiesUnderTheServerCanonicalForm()
    {
        var prev = AuditCanonicalForm.GenesisHash();
        foreach (var row in LoadFixture())
        {
            var evt = new AuditEvent(row.EventType, row.RoomId, row.MessageSeq, row.ActorId, row.SubjectId, row.Value, row.Time);
            var hash = AuditCanonicalForm.Hash(prev, AuditCanonicalForm.Compute(evt));
            Assert.Equal(row.Hash, Convert.ToHexStringLower(hash));
            prev = hash;
        }
    }

    [Fact]
    public void Fixture_VerifiesUnderTheSdkPort()
    {
        var prev = AuditChain.GenesisHash();
        foreach (var row in LoadFixture())
        {
            var hash = AuditChain.Hash(prev, AuditChain.Compute(row));
            Assert.Equal(row.Hash, Convert.ToHexStringLower(hash));
            prev = hash;
        }
    }

    [Fact]
    public async Task ArchiveAuditLog_TamperedValueFailsNamingThatId()
    {
        var rows = LoadFixture();
        rows[2] = rows[2] with { Value = 31 };
        var client = MakeClient(_ => Json(new { events = rows, hasMore = false }));
        using var stream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => client.ArchiveAuditLogAsync(stream));

        Assert.Contains("id 3", ex.Message);
        Assert.Equal(2, Lines(stream).Length);
    }

    [Fact]
    public async Task ArchiveAuditLog_WritesVerifiedEntriesThenCheckpoints()
    {
        var rows = LoadFixture();
        var client = MakeClient(req => req.RequestUri!.AbsolutePath switch
        {
            "/audit/" => Json(new { events = rows, hasMore = false }),
            "/audit/checkpoints" => Json(new { checkpoints = new[] { new { day = "2026-09-07", lastId = 3, hash = rows[2].Hash, createdAt = "2026-09-07T12:37:00Z" } } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var stream = new MemoryStream();

        var result = await client.ArchiveAuditLogAsync(stream);

        Assert.Equal(new AuditArchiveResult(3, 3, rows[2].Hash), result);
        var lines = Lines(stream);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("{\"checkpoint\":", lines[3]);
    }

    [Fact]
    public async Task ArchiveAuditLog_AfterIdWithoutPreviousHashIsAnArgumentError()
    {
        var client = MakeClient(_ => throw new InvalidOperationException("no request expected"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ArchiveAuditLogAsync(new MemoryStream(), afterId: 1));
    }

    [Fact]
    public async Task ExportRoomHistory_PagesForwardOldestFirstAndReturnsCount()
    {
        var afterSeqs = new List<string?>();
        var pages = new Dictionary<string, (ulong[] Seqs, bool HasMore)>
        {
            ["0"] = ([1, 2], true),
            ["2"] = ([3, 4], true),
            ["4"] = ([5], false),
        };
        var client = MakeClient(req =>
        {
            var afterSeq = AfterSeqOf(req);
            afterSeqs.Add(afterSeq);
            var (seqs, hasMore) = pages[afterSeq!];
            return Json(new
            {
                roomId = RoomId,
                messages = seqs.Select(seq => new { seq, time = "2026-09-07T12:00:00Z", senderId = "dave", content = $"m{seq}" }),
                hasMore,
            });
        });
        using var stream = new MemoryStream();

        var count = await client.ExportRoomHistoryAsync(RoomId, "dave", stream);

        Assert.Equal(5, count);
        Assert.Equal(new[] { "0", "2", "4" }, afterSeqs);
        var seqs = Lines(stream).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetUInt64());
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, seqs);
    }

    private static AuditEntry[] LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-chain.json");
        return JsonSerializer.Deserialize<AuditEntry[]>(File.ReadAllText(path), CamelCase)!;
    }

    private static string[] Lines(MemoryStream stream) =>
        Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string? AfterSeqOf(HttpRequestMessage req)
    {
        foreach (var pair in req.RequestUri!.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=');
            if (parts[0] == "afterSeq")
                return parts[1];
        }
        return null;
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, CamelCase), Encoding.UTF8, "application/json"),
    };

    private static DotwireHostClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };
        var options = new DotwireHostOptions
        {
            BaseUrl = http.BaseAddress,
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            KeyId = TestKeys.Kid,
            PrivateKeyPem = TestKeys.PrivatePem,
        };
        return new DotwireHostClient(http, new DotwireTokenSigner(options), options);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
