using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dotwire.Host;
using Dotwire.Host.Moderation;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Pure SDK-side coverage for <see cref="DotwireHostClient"/> that needs no running dotwire
/// server: error mapping, role-resolution retry-once, hooks (in-process and inside the
/// webhook handlers), <see cref="DotwireHostClient.HandleClientSendAsync"/> result shapes,
/// webhook signature verification, and the SSE parser against a hand-written wire stream.
/// Server routing/auth-gate coverage against a real dotwire lives in
/// <see cref="DotwireHostClientComposeTests"/>.
/// </summary>
public class DotwireHostClientUnitTests
{
    private static (DotwireHostClient Client, StubHandler Handler) MakeClient(
        string? webhookSecret = null, bool allowUnsigned = true, bool allowLegacy = false)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };
        var options = new DotwireHostOptions
        {
            BaseUrl = http.BaseAddress,
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            KeyId = TestKeys.Kid,
            PrivateKeyPem = TestKeys.PrivatePem,
            WebhookSecret = webhookSecret,
            AllowUnsignedWebhooks = allowUnsigned,
            AllowLegacyWebhookSignatures = allowLegacy,
            RoleCacheTtl = TimeSpan.FromMinutes(5),
        };
        var signer = new DotwireTokenSigner(options);
        return (new DotwireHostClient(http, signer, options), handler);
    }

    // ---- error mapping -------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, DotwireErrorKind.BadRequest, null)]
    [InlineData(HttpStatusCode.Unauthorized, DotwireErrorKind.Unauthorized, null)]
    [InlineData(HttpStatusCode.Forbidden, DotwireErrorKind.Forbidden, null)]
    [InlineData(HttpStatusCode.NotFound, DotwireErrorKind.NotFound, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DotwireErrorKind.Unavailable, "stream_unavailable")]
    public async Task RedactMessageAsync_MapsBareAndCodedErrors(HttpStatusCode status, DotwireErrorKind expectedKind, string? errorCode)
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ =>
        {
            var body = errorCode is null ? null : $$"""{"error":"{{errorCode}}"}""";
            return MakeResponse(status, body);
        });

        var ex = await Assert.ThrowsAsync<DotwireApiException>(() =>
            client.RedactMessageAsync(Guid.NewGuid(), 1, TestContext.Current.CancellationToken));

        Assert.Equal(expectedKind, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        if (errorCode is not null)
            Assert.Equal(errorCode, ex.ErrorCode);
    }

    [Fact]
    public async Task RedactMessageAsync_PresendRejected_MapsToPresendRejectedKind()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ => MakeResponse(HttpStatusCode.UnprocessableEntity, """{"error":"presend_rejected","reason":"blocked term"}"""));

        var ex = await Assert.ThrowsAsync<DotwireApiException>(() =>
            client.RedactMessageAsync(Guid.NewGuid(), 1, TestContext.Current.CancellationToken));

        Assert.Equal(DotwireErrorKind.PresendRejected, ex.Kind);
        Assert.Equal("presend_rejected", ex.ErrorCode);
        Assert.Equal("blocked term", ex.Reason);
    }

    [Fact]
    public async Task RedactMessageAsync_RateLimited_ParsesRetryAfter()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ =>
        {
            var response = MakeResponse(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var ex = await Assert.ThrowsAsync<DotwireApiException>(() =>
            client.RedactMessageAsync(Guid.NewGuid(), 1, TestContext.Current.CancellationToken));

        Assert.Equal(DotwireErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    // ---- role resolution -------------------------------------------------------

    [Fact]
    public async Task GetRoomParticipantsAsync_UsesMemberTokenAndReturnsIds()
    {
        var (client, handler) = MakeClient();
        var roomId = Guid.NewGuid();
        string? requestPath = null;
        string? tokenSubject = null;

        handler.Respond(req =>
        {
            requestPath = req.RequestUri!.AbsolutePath;
            var token = req.Headers.Authorization!.Parameter!;
            var payload = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(token.Split('.')[1]);
            using var json = JsonDocument.Parse(payload);
            tokenSubject = json.RootElement.GetProperty("sub").GetString();
            return MakeResponse(HttpStatusCode.OK, """["user-1","user-2"]""");
        });

        var userIds = await client.GetRoomParticipantsAsync(roomId, "user-1", TestContext.Current.CancellationToken);

        Assert.Equal($"/rooms/{roomId}/participants", requestPath);
        Assert.Equal("user-1", tokenSubject);
        Assert.Equal(["user-1", "user-2"], userIds);
    }

    [Fact]
    public async Task SendAsUserAsync_ResolvesRoleAfter403_ThenSucceeds()
    {
        var (client, handler) = MakeClient();
        var roomId = Guid.NewGuid();
        var calls = new List<(HttpMethod Method, string Path)>();

        handler.Respond(req =>
        {
            calls.Add((req.Method!, req.RequestUri!.AbsolutePath));
            if (req.RequestUri.AbsolutePath == $"/rooms/{roomId}/messages" && calls.Count(c => c.Path.Contains("/messages")) == 1)
                return MakeResponse(HttpStatusCode.Forbidden, null);

            if (req.RequestUri.AbsolutePath == "/admin/users/user-1/role")
                return MakeResponse(HttpStatusCode.OK, """{"userId":"user-1","role":"admin"}""");

            return MakeResponse(HttpStatusCode.Accepted, """{"seq":1,"time":"2026-01-01T00:00:00Z"}""");
        });

        var ack = await client.SendAsUserAsync(roomId, "user-1", "hello", TestContext.Current.CancellationToken);

        Assert.Equal(1UL, ack.Seq);
        Assert.Equal(3, calls.Count); // 403, role lookup, retry
    }

    [Fact]
    public async Task SendAsUserAsync_SecondForbidden_SurfacesAsForbidden()
    {
        var (client, handler) = MakeClient();
        var roomId = Guid.NewGuid();

        handler.Respond(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/admin/users/user-1/role")
                return MakeResponse(HttpStatusCode.OK, """{"userId":"user-1","role":"admin"}""");

            return MakeResponse(HttpStatusCode.Forbidden, null);
        });

        var ex = await Assert.ThrowsAsync<DotwireApiException>(() =>
            client.SendAsUserAsync(roomId, "user-1", "hello", TestContext.Current.CancellationToken));

        Assert.Equal(DotwireErrorKind.Forbidden, ex.Kind);
    }

    // ---- hooks -------------------------------------------------------

    [Fact]
    public async Task SendAsUserAsync_PresendReject_ThrowsWithoutHittingWire()
    {
        var (client, handler) = MakeClient();
        var called = false;
        handler.Respond(_ => { called = true; return MakeResponse(HttpStatusCode.Accepted, """{"seq":1,"time":"2026-01-01T00:00:00Z"}"""); });
        client.Presend = (_, _) => Task.FromResult(PresendResult.Reject("blocked"));

        var ex = await Assert.ThrowsAsync<PresendRejectedException>(() =>
            client.SendAsUserAsync(Guid.NewGuid(), "user-1", "bad word", TestContext.Current.CancellationToken));

        Assert.Equal("blocked", ex.Reason);
        Assert.False(called);
    }

    [Fact]
    public async Task SendAsUserAsync_PresendRewrite_ForwardsRewrittenContent()
    {
        var (client, handler) = MakeClient();
        string? sentContent = null;
        handler.Respond(async req =>
        {
            var json = await req.Content!.ReadAsStringAsync();
            sentContent = JsonDocument.Parse(json).RootElement.GetProperty("content").GetString();
            return MakeResponse(HttpStatusCode.Accepted, """{"seq":1,"time":"2026-01-01T00:00:00Z"}""");
        });
        client.Presend = (_, _) => Task.FromResult(PresendResult.Accept("REDACTED"));

        await client.SendAsUserAsync(Guid.NewGuid(), "user-1", "bad word", TestContext.Current.CancellationToken);

        Assert.Equal("REDACTED", sentContent);
    }

    [Fact]
    public async Task SendAsUserAsync_PostSendThrows_SurfacesPostSendExceptionWithAck()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ => MakeResponse(HttpStatusCode.Accepted, """{"seq":42,"time":"2026-01-01T00:00:00Z"}"""));
        client.PostSend = (_, _) => throw new InvalidOperationException("moderation service down");

        var ex = await Assert.ThrowsAsync<PostSendException>(() =>
            client.SendAsUserAsync(Guid.NewGuid(), "user-1", "hello", TestContext.Current.CancellationToken));

        Assert.Equal(42UL, ex.Sent.Seq);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task SendSystemMessageAsync_NeverRunsHooks()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ => MakeResponse(HttpStatusCode.Accepted, """{"seq":1,"time":"2026-01-01T00:00:00Z"}"""));
        var presendCalled = false;
        var postSendCalled = false;
        client.Presend = (_, _) => { presendCalled = true; return Task.FromResult(PresendResult.Accept()); };
        client.PostSend = (_, _) => { postSendCalled = true; return Task.CompletedTask; };

        await client.SendSystemMessageAsync(Guid.NewGuid(), "announcement", ct: TestContext.Current.CancellationToken);

        Assert.False(presendCalled);
        Assert.False(postSendCalled);
    }

    // ---- HandleClientSendAsync -------------------------------------------------------

    [Fact]
    public async Task HandleClientSendAsync_Success_ReturnsAckJson()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ => MakeResponse(HttpStatusCode.Accepted, """{"seq":7,"time":"2026-01-01T00:00:00Z"}"""));

        var roomId = Guid.NewGuid();
        await using var body = JsonBody($$"""{"roomId":"{{roomId}}","content":"hi"}""");
        var result = await client.HandleClientSendAsync(body, "user-1", TestContext.Current.CancellationToken);

        Assert.Equal(202, result.StatusCode);
        Assert.NotNull(result.Ack);
        Assert.Equal(7UL, result.Ack!.Seq);
        Assert.Contains("\"seq\":7", result.Json);
    }

    [Fact]
    public async Task HandleClientSendAsync_EmptyContent_Returns400()
    {
        var (client, _) = MakeClient();
        await using var body = JsonBody("""{"roomId":"11111111-1111-1111-1111-111111111111","content":""}""");

        var result = await client.HandleClientSendAsync(body, "user-1", TestContext.Current.CancellationToken);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("invalid_content", result.ErrorCode);
    }

    [Fact]
    public async Task HandleClientSendAsync_PresendRejected_Returns422WithReason()
    {
        var (client, _) = MakeClient();
        client.Presend = (_, _) => Task.FromResult(PresendResult.Reject("blocked term"));
        var roomId = Guid.NewGuid();
        await using var body = JsonBody($$"""{"roomId":"{{roomId}}","content":"bad"}""");

        var result = await client.HandleClientSendAsync(body, "user-1", TestContext.Current.CancellationToken);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal("presend_rejected", result.ErrorCode);
        Assert.Equal("blocked term", result.Reason);
    }

    [Fact]
    public async Task HandleClientSendAsync_ServerRateLimited_MapsStatusCode()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ =>
        {
            var response = MakeResponse(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return response;
        });
        var roomId = Guid.NewGuid();
        await using var body = JsonBody($$"""{"roomId":"{{roomId}}","content":"hi"}""");

        var result = await client.HandleClientSendAsync(body, "user-1", TestContext.Current.CancellationToken);

        Assert.Equal(429, result.StatusCode);
        Assert.Equal("rate_limited", result.ErrorCode);
    }

    // ---- webhook handlers -------------------------------------------------------

    [Fact]
    public async Task HandlePresendWebhookAsync_NoSignatureConfigured_DefaultAllowsWhenNoHook()
    {
        var (client, _) = MakeClient();
        var roomId = Guid.NewGuid();
        await using var body = JsonBody($$"""{"roomId":"{{roomId}}","senderId":"user-1","content":"hi","time":"2026-01-01T00:00:00Z","source":"member"}""");

        var result = await client.HandlePresendWebhookAsync(body, (string?)null, TestContext.Current.CancellationToken);

        Assert.Equal(200, result.StatusCode);
        using var doc = JsonDocument.Parse(result.Json);
        Assert.True(doc.RootElement.GetProperty("allow").GetBoolean());
    }

    [Fact]
    public async Task HandlePresendWebhookAsync_RunsPresendHook_RewriteAndReject()
    {
        var (client, _) = MakeClient();
        client.Presend = (ctx, _) => Task.FromResult(
            ctx.Content == "bad" ? PresendResult.Reject("blocked") : PresendResult.Accept("rewritten"));
        var roomId = Guid.NewGuid();

        await using (var okBody = JsonBody($$"""{"roomId":"{{roomId}}","senderId":"user-1","content":"ok","time":"2026-01-01T00:00:00Z","source":"member"}"""))
        {
            var allowed = await client.HandlePresendWebhookAsync(okBody, (string?)null, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(allowed.Json);
            Assert.True(doc.RootElement.GetProperty("allow").GetBoolean());
            Assert.Equal("rewritten", doc.RootElement.GetProperty("content").GetString());
        }

        await using (var badBody = JsonBody($$"""{"roomId":"{{roomId}}","senderId":"user-1","content":"bad","time":"2026-01-01T00:00:00Z","source":"member"}"""))
        {
            var rejected = await client.HandlePresendWebhookAsync(badBody, (string?)null, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(rejected.Json);
            Assert.False(doc.RootElement.GetProperty("allow").GetBoolean());
            Assert.Equal("blocked", doc.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task HandlePostSendWebhookAsync_RunsPostSendHook()
    {
        var (client, _) = MakeClient();
        PostSendContext? seen = null;
        client.PostSend = (ctx, _) => { seen = ctx; return Task.CompletedTask; };
        var roomId = Guid.NewGuid();
        await using var body = JsonBody($$"""{"roomId":"{{roomId}}","seq":9,"senderId":"user-1","content":"hi","time":"2026-01-01T00:00:00Z","source":"member"}""");

        var result = await client.HandlePostSendWebhookAsync(body, (string?)null, TestContext.Current.CancellationToken);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{}", result.Json);
        Assert.NotNull(seen);
        Assert.Equal(9UL, seen!.Seq);
        Assert.Equal(roomId, seen.RoomId);
    }

    [Fact]
    public async Task PostSendWebhook_And_ClientSend_ShareOnePostSendDelegate_ModerationQueueSeesBoth()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ => MakeResponse(HttpStatusCode.Accepted, """{"seq":1,"time":"2026-01-01T00:00:00Z"}"""));
        var queue = new ModerationQueue();
        client.PostSend = queue.PostSendHook;

        var roomId = Guid.NewGuid();
        await using (var body = JsonBody($$"""{"roomId":"{{roomId}}","content":"hi"}"""))
            await client.HandleClientSendAsync(body, "user-1", TestContext.Current.CancellationToken);

        await using (var webhookBody = JsonBody($$"""{"roomId":"{{roomId}}","seq":2,"senderId":"user-2","content":"hi","time":"2026-01-01T00:00:00Z","source":"member"}"""))
            await client.HandlePostSendWebhookAsync(webhookBody, (string?)null, TestContext.Current.CancellationToken);

        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal(1UL, first.Seq);
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal(2UL, second.Seq);
    }

    [Fact]
    public async Task WebhookHandlers_SignatureVerification_CorrectAndIncorrect()
    {
        const string secret = "test-secret";
        var (client, _) = MakeClient(secret, allowUnsigned: false, allowLegacy: true);
        var roomId = Guid.NewGuid();
        var json = $$"""{"roomId":"{{roomId}}","senderId":"user-1","content":"hi","time":"2026-01-01T00:00:00Z","source":"member"}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var correctSig = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes)).ToLowerInvariant();

        await using (var body = new MemoryStream(bytes))
        {
            var ok = await client.HandlePresendWebhookAsync(body, correctSig, TestContext.Current.CancellationToken);
            Assert.Equal(200, ok.StatusCode);
        }

        await using (var body = new MemoryStream(bytes))
        {
            var bad = await client.HandlePresendWebhookAsync(body, "sha256=deadbeef", TestContext.Current.CancellationToken);
            Assert.Equal(401, bad.StatusCode);
        }

        await using (var body = new MemoryStream(bytes))
        {
            var missing = await client.HandlePresendWebhookAsync(body, (string?)null, TestContext.Current.CancellationToken);
            Assert.Equal(401, missing.StatusCode);
        }
    }

    // ---- SSE parser -------------------------------------------------------

    [Fact]
    public async Task StreamEventsAsync_ParsesAllEventTypes_AndStopsOnRevoked()
    {
        var (client, handler) = MakeClient();
        var roomId = Guid.NewGuid();
        var sse =
            "id: 1\n" +
            "event: message\n" +
            $"data: {{\"roomId\":\"{roomId}\",\"seq\":1,\"senderId\":\"a\",\"time\":\"2026-01-01T00:00:00Z\",\"content\":\"hi\",\"replayed\":false}}\n\n" +
            "event: typing\n" +
            $"data: {{\"roomId\":\"{roomId}\",\"userId\":\"a\",\"isTyping\":true}}\n\n" +
            "event: presence\n" +
            $"data: {{\"roomId\":\"{roomId}\",\"joined\":[\"a\"],\"left\":[]}}\n\n" +
            "id: 2\n" +
            "event: retracted\n" +
            $"data: {{\"roomId\":\"{roomId}\",\"seq\":2}}\n\n" +
            "event: revoked\n" +
            $"data: {{\"roomId\":\"{roomId}\"}}\n\n";

        var callCount = 0;
        handler.Respond(_ =>
        {
            callCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        });

        var events = new List<RoomEvent>();
        await foreach (var evt in client.StreamEventsAsync(roomId, "user-1", ct: TestContext.Current.CancellationToken))
            events.Add(evt);

        Assert.Equal(1, callCount); // never reconnects after revoked
        Assert.Collection(events,
            e => Assert.IsType<MessageEvent>(e),
            e => Assert.IsType<TypingEvent>(e),
            e => Assert.IsType<PresenceEvent>(e),
            e => Assert.IsType<RetractedEvent>(e),
            e => Assert.IsType<RevokedEvent>(e));

        var message = Assert.IsType<MessageEvent>(events[0]);
        Assert.Equal(1UL, message.Seq);
        Assert.Equal("hi", message.Content);
    }

    [Fact]
    public async Task StreamEventsAsync_ResumesWithLastEventId_AfterDrop()
    {
        var (client, handler) = MakeClient();
        var roomId = Guid.NewGuid();
        var attempts = new List<string?>();

        handler.Respond(req =>
        {
            attempts.Add(req.Headers.TryGetValues("Last-Event-ID", out var v) ? v.First() : null);
            string sse;
            if (attempts.Count == 1)
            {
                sse = "event: message\n" +
                      $"data: {{\"roomId\":\"{roomId}\",\"seq\":5,\"senderId\":\"a\",\"time\":\"2026-01-01T00:00:00Z\",\"content\":\"hi\",\"replayed\":false}}\n\n";
                // Stream ends abruptly here (EOF) - simulates a dropped connection.
            }
            else
            {
                sse = "event: revoked\n" + $"data: {{\"roomId\":\"{roomId}\"}}\n\n";
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        });

        var events = new List<RoomEvent>();
        await foreach (var evt in client.StreamEventsAsync(roomId, "user-1", ct: TestContext.Current.CancellationToken))
            events.Add(evt);

        Assert.Equal(2, attempts.Count);
        Assert.Null(attempts[0]);
        Assert.Equal("5", attempts[1]); // resumed with the last seen seq
        Assert.IsType<MessageEvent>(events[0]);
        Assert.IsType<RevokedEvent>(events[1]);
    }

    // ---- helpers -------------------------------------------------------

    private static HttpResponseMessage MakeResponse(HttpStatusCode status, string? json)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return response;
    }

    private static MemoryStream JsonBody(string json) => new(Encoding.UTF8.GetBytes(json));

    private sealed class StubHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _handler;

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = req => Task.FromResult(handler(req));

        public void Respond(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler is null
                ? throw new InvalidOperationException("No stub response configured.")
                : _handler(request);
    }
}

public class DotwireHostClientSecurityTests
{
    private const string Secret = "test-secret";

    private static (DotwireHostClient Client, RecordingHandler Handler) MakeClient(
        string? webhookSecret = Secret, bool allowUnsigned = false, bool allowLegacy = false, int maxBody = 1024 * 1024)
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test.local") };
        var options = new DotwireHostOptions
        {
            BaseUrl = http.BaseAddress,
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            KeyId = TestKeys.Kid,
            PrivateKeyPem = TestKeys.PrivatePem,
            WebhookSecret = webhookSecret,
            AllowUnsignedWebhooks = allowUnsigned,
            AllowLegacyWebhookSignatures = allowLegacy,
            MaxWebhookBodyBytes = maxBody,
        };
        return (new DotwireHostClient(http, new DotwireTokenSigner(options), options), handler);
    }

    private static byte[] PresendBody() =>
        Encoding.UTF8.GetBytes($$"""{"roomId":"{{Guid.NewGuid()}}","senderId":"user-1","content":"hi","time":"2026-01-01T00:00:00Z","source":"member"}""");

    private static string Sign(byte[] payload) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), payload));

    private static WebhookHeaders V2(byte[] body, DateTimeOffset at)
    {
        var ts = at.ToUnixTimeSeconds().ToString();
        return new WebhookHeaders(Sign(body), ts, Sign(Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Webhooks_WithoutSecret_RefuseToRun()
    {
        var (client, _) = MakeClient(webhookSecret: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.HandlePresendWebhookAsync(new MemoryStream(PresendBody()), (string?)null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.HandlePostSendWebhookAsync(new MemoryStream(PresendBody()), (string?)null, Ct));
    }

    [Fact]
    public async Task Webhooks_TimestampedSignature_IsAccepted()
    {
        var (client, _) = MakeClient();
        var body = PresendBody();

        var result = await client.HandlePresendWebhookAsync(new MemoryStream(body), V2(body, DateTimeOffset.UtcNow), Ct);

        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Webhooks_StaleTimestamp_IsRejected()
    {
        var (client, _) = MakeClient();
        var body = PresendBody();

        var result = await client.HandlePresendWebhookAsync(new MemoryStream(body), V2(body, DateTimeOffset.UtcNow.AddMinutes(-10)), Ct);

        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task Webhooks_TamperedTimestamp_IsRejected()
    {
        var (client, _) = MakeClient();
        var body = PresendBody();
        var headers = V2(body, DateTimeOffset.UtcNow.AddMinutes(-10)) with { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() };

        var result = await client.HandlePresendWebhookAsync(new MemoryStream(body), headers, Ct);

        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task Webhooks_LegacyBodyOnlySignature_NeedsOptIn()
    {
        var body = PresendBody();
        var (strict, _) = MakeClient();
        var (legacy, _) = MakeClient(allowLegacy: true);

        var rejected = await strict.HandlePresendWebhookAsync(new MemoryStream(body), Sign(body), Ct);
        var accepted = await legacy.HandlePresendWebhookAsync(new MemoryStream(body), Sign(body), Ct);

        Assert.Equal(401, rejected.StatusCode);
        Assert.Equal(200, accepted.StatusCode);
    }

    [Fact]
    public async Task Webhooks_OversizedBody_Is413()
    {
        var (client, _) = MakeClient(maxBody: 64);
        var body = PresendBody();

        var result = await client.HandlePostSendWebhookAsync(new MemoryStream(body), V2(body, DateTimeOffset.UtcNow), Ct);

        Assert.Equal(413, result.StatusCode);
    }

    [Fact]
    public async Task ClientSend_OversizedBody_Is413()
    {
        var (client, handler) = MakeClient(maxBody: 16);

        var result = await client.HandleClientSendAsync(new MemoryStream(PresendBody()), "user-1", Ct);

        Assert.Equal(413, result.StatusCode);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SendAsUser_ForwardsIdempotencyKey()
    {
        var (client, handler) = MakeClient();
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("""{"seq":1,"time":"2026-01-01T00:00:00Z"}""", Encoding.UTF8, "application/json"),
        });

        await client.SendAsUserAsync(Guid.NewGuid(), "user-1", "hi", "retry-key-1", Ct);

        Assert.Equal("retry-key-1", handler.Calls.Single().IdempotencyKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task StreamEvents_StopsOnTerminalStatus(HttpStatusCode status)
    {
        var (client, handler) = MakeClient();
        handler.Respond(req => req.RequestUri!.AbsolutePath.Contains("/admin/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(status));

        await Assert.ThrowsAsync<DotwireApiException>(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync(Guid.NewGuid(), "user-1", new StreamOptions(), Ct))
            {
            }
        });

        Assert.True(handler.Calls.Count <= 2);
    }

    [Fact]
    public async Task StreamEvents_SkipsMalformedEvents()
    {
        var (client, handler) = MakeClient();
        var roomId = Guid.NewGuid();
        var sse =
            "event: message\ndata: {not json\n\n" +
            $"event: message\ndata: {{\"roomId\":\"{roomId}\",\"seq\":2,\"senderId\":\"a\",\"time\":\"2026-01-01T00:00:00Z\",\"content\":\"ok\",\"replayed\":false}}\n\n" +
            $"event: revoked\ndata: {{\"roomId\":\"{roomId}\"}}\n\n";
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) });

        var events = new List<RoomEvent>();
        await foreach (var evt in client.StreamEventsAsync(roomId, "user-1", new StreamOptions(), Ct))
            events.Add(evt);

        Assert.Collection(events,
            e => Assert.Equal(2UL, Assert.IsType<MessageEvent>(e).Seq),
            e => Assert.IsType<RevokedEvent>(e));
    }

    [Fact]
    public void MintToken_RejectsUnknownRoles()
    {
        var (client, _) = MakeClient();

        Assert.Throws<ArgumentException>(() => client.MintToken("user-1", "root"));
    }

    [Fact]
    public void Options_ToStringRedactsSecrets()
    {
        var options = new DotwireHostOptions
        {
            BaseUrl = new Uri("http://test.local"),
            Issuer = "i",
            KeyId = "k",
            PrivateKeyPem = "-----BEGIN PRIVATE KEY-----secret",
            WebhookSecret = "hook-secret",
        };

        var text = options.ToString();

        Assert.DoesNotContain("hook-secret", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public async Task ModerationQueue_FullQueue_TimesOutInsteadOfBlockingForever()
    {
        var queue = new ModerationQueue(capacity: 1, enqueueTimeout: TimeSpan.FromMilliseconds(50));
        var item = new PendingModeration(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "u1", "hi");
        await queue.EnqueueAsync(item, Ct);

        await Assert.ThrowsAsync<TimeoutException>(async () => await queue.EnqueueAsync(item, Ct));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<(string Path, string? IdempotencyKey)> Calls { get; } = [];

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null));
            return Task.FromResult(_respond(request));
        }
    }
}
