# Dotwire.Host (.NET)

For coding agents adding dotwire to an application, see the [SDK integration guide](../docs/SDK_AGENT_GUIDE.md).

Host-side SDK: mints RS256 tokens with the host's private key and talks to dotwire over
HTTP. Every message reaches dotwire *through the host backend*, which is what makes the
moderation hooks below both cheap and complete. Targets `net8.0`, `net9.0` and `net10.0`.

```csharp
var options = new DotwireHostOptions
{
    BaseUrl = new Uri("http://localhost:8080"),
    Issuer = "my-host",
    KeyId = "host-key-1",
    PrivateKeyPem = File.ReadAllText("host-jwt.key"),
};
var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
{
    BaseAddress = options.BaseUrl,
};
IDotwireHostClient client = new DotwireHostClient(http, new DotwireTokenSigner(options), options);

var ack = await client.SendAsUserAsync(roomId, "user-42", "hello");
// ack.Seq is the JetStream ordering token - the handle a later redaction targets.

var participantIds = await client.GetRoomParticipantsAsync(roomId, "user-42");
// The caller must be a room member. Resolve names and avatars in the host app.
```

## Dependency injection (`AddDotwireHost`) - requires two extra packages

The SDK itself depends only on `Microsoft.IdentityModel.JsonWebTokens`/`Tokens`, so it stays
usable in any .NET app without pulling in ASP.NET Core or the generic host. Real DI
registration (`services.AddDotwireHost(...)`) needs `Microsoft.Extensions.DependencyInjection.Abstractions`
and `Microsoft.Extensions.Configuration.Abstractions`, which this project deliberately does
**not** reference - add them yourself if you want the extension method:

```bash
dotnet add package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add package Microsoft.Extensions.Configuration.Abstractions
```

Until those are referenced by your project, `Dotwire.Host` ships without an
`AddDotwireHost` method at all - there is nothing to call. Once you've added the packages,
wire it up yourself with the same shape this SDK would otherwise generate for you (config
keys match `DotwireHostOptions` property names exactly, no reflection binder):

```csharp
public static class DotwireHostServiceCollectionExtensions
{
    public static IServiceCollection AddDotwireHost(this IServiceCollection services, IConfiguration section)
    {
        var options = new DotwireHostOptions
        {
            BaseUrl = new Uri(section["BaseUrl"]!),
            Issuer = section["Issuer"]!,
            Audience = section["Audience"] ?? "dotwire",
            KeyId = section["KeyId"]!,
            PrivateKeyPem = section["PrivateKeyPem"]!,
            AdminUserId = section["AdminUserId"] ?? DotwireHostOptions.DefaultAdminUserId,
            AuditorUserId = section["AuditorUserId"] ?? DotwireHostOptions.DefaultAuditorUserId,
            DefaultTokenTtl = section["DefaultTokenTtl"] is { } ttl ? TimeSpan.Parse(ttl) : TimeSpan.FromMinutes(15),
            RequestTimeout = section["RequestTimeout"] is { } rt ? TimeSpan.Parse(rt) : TimeSpan.FromSeconds(15),
            WebhookSecret = section["WebhookSecret"],
            RoleCacheTtl = section["RoleCacheTtl"] is { } rc ? TimeSpan.Parse(rc) : TimeSpan.FromMinutes(5),
        };
        return services.AddDotwireHost(o =>
        {
            o.BaseUrl = options.BaseUrl; o.Issuer = options.Issuer; o.Audience = options.Audience;
            o.KeyId = options.KeyId; o.PrivateKeyPem = options.PrivateKeyPem;
            o.AdminUserId = options.AdminUserId; o.AuditorUserId = options.AuditorUserId;
            o.DefaultTokenTtl = options.DefaultTokenTtl; o.RequestTimeout = options.RequestTimeout;
            o.WebhookSecret = options.WebhookSecret; o.RoleCacheTtl = options.RoleCacheTtl;
        });
    }

    public static IServiceCollection AddDotwireHost(this IServiceCollection services, Action<DotwireHostOptions> configure)
    {
        var options = new DotwireHostOptions { BaseUrl = null!, Issuer = null!, KeyId = null!, PrivateKeyPem = null! };
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new DotwireTokenSigner(sp.GetRequiredService<DotwireHostOptions>()));
        services.AddSingleton<HttpClient>(_ => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        { BaseAddress = options.BaseUrl });
        services.AddSingleton<IDotwireHostClient>(sp => new DotwireHostClient(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<DotwireTokenSigner>(), options));
        return services;
    }
}
```

## Identity rules

- Roles/membership/injection/redaction/DSAR/retention are minted as `AdminUserId`
  (default `host-admin`, preseeded by migration 0004 with the `admin` role).
- Audit read/verify/checkpoints are minted as `AuditorUserId` (default `host-auditor` -
  give it the `auditor` role yourself, e.g. `await client.SetUserRoleAsync("host-auditor", "auditor")`).
- Send-as-user / history-as-user / SSE-as-user / `HandleClientSendAsync` mint as that user
  id and resolve role automatically: try `member` first, and on a `403` look the role up via
  `GET /admin/users/{userId}/role`, cache it for `RoleCacheTtl`, and retry once. A second
  `403` surfaces as `DotwireApiException` with `Kind == Forbidden`.
- `MintToken(userId, role, ...)` bypasses resolution entirely - you assert the role.

Admin and auditor tokens are cached internally and re-minted one minute before their own
expiry, so calling admin/auditor-scoped methods in a hot loop doesn't re-mint a token per call.

## Moderation: two hooks, two speeds

| Hook | When it runs | Cost it adds to a send | Put here |
|---|---|---|---|
| `Presend` | before the HTTP call (direct SDK sends) or before the webhook responds (server call-outs) | its own runtime, *blocking* | cheap, local, deterministic checks: blocklists/regex, length caps, per-user rate limits |
| `PostSend` | after dotwire's `202`/postsend call-out, with `Seq` | a channel write (µs) | hand-off to asynchronous review - anything that calls a network service |

Both hooks run in **four** places, so one implementation covers every path a message can
take through your host: `SendAsUserAsync`, `HandleClientSendAsync` (your own client-send
endpoint), and `HandlePresendWebhookAsync`/`HandlePostSendWebhookAsync` (dotwire's own
webhook call-outs, if you've pointed `Dotwire:Webhooks:Presend:Url`/`PostSend:Url` at your
host instead of calling the SDK directly). They never run for `SendSystemMessageAsync`
(host-authored injection).

`Presend` can reject (`PresendRejectedException` from `SendAsUserAsync`; a `422`
`presend_rejected` webhook/`HandleClientSendAsync` response otherwise) or rewrite content.
`PostSend` runs only for accepted messages, because only those have a `(RoomId, Seq)` that a
verdict can act on; if it throws from `SendAsUserAsync` you get `PostSendException` carrying
the ack (the send already happened - never retry it). The webhook handler variant swallows a
throwing `PostSend` instead, since the server's own postsend contract is best-effort and the
send it describes has already happened either way.

```csharp
client.Presend = (ctx, ct) => Task.FromResult(
    Blocklist.Hits(ctx.Content) ? PresendResult.Reject("blocked term") : PresendResult.Accept());

var queue = new ModerationQueue();          // bounded; back-pressures sends when full
client.PostSend = queue.PostSendHook;
```

### Client-send and webhook handlers

If your host exposes its own "send a message" endpoint for browser/mobile clients (rather
than having them call dotwire directly), route the request body through
`HandleClientSendAsync` and write its `Json`/`StatusCode` straight back:

```csharp
app.MapPost("/my-app/rooms/{roomId}/send", async (HttpRequest req, string userId) =>
{
    var result = await client.HandleClientSendAsync(req.Body, userId);
    return Results.Text(result.Json, "application/json", statusCode: result.StatusCode);
});
```

If instead you've configured dotwire's own `Dotwire:Webhooks:Presend:Url`/`PostSend:Url` to
point at your host, wire the same `Presend`/`PostSend` delegates through the matching
handlers - signature verification (`X-Dotwire-Signature: sha256=<hex>`, HMAC-SHA256 of the
raw body against `DotwireHostOptions.WebhookSecret`) is handled for you:

```csharp
app.MapPost("/webhooks/presend", async (HttpRequest req) =>
{
    var result = await client.HandlePresendWebhookAsync(req.Body, req.Headers["X-Dotwire-Signature"]);
    return Results.Text(result.Json, "application/json", statusCode: result.StatusCode);
});

app.MapPost("/webhooks/postsend", async (HttpRequest req) =>
{
    var result = await client.HandlePostSendWebhookAsync(req.Body, req.Headers["X-Dotwire-Signature"]);
    return Results.Text(result.Json, "application/json", statusCode: result.StatusCode);
});
```

### Batched asynchronous review

`BatchModerationWorker` drains the queue in size-or-time bounded batches, asks an
`IModerationClassifier` for verdicts, and runs an action for each flagged one - by default,
physical redaction through the admin API, retrying up to three times on a `429` and honoring
the server's `Retry-After`:

```csharp
IModerationClassifier classifier = new ClaudeBatchModerationClassifier(anthropic, policyPrompt);
var worker = new BatchModerationWorker(
    queue,
    classifier,
    onFlagged: BatchModerationWorker.RedactWith(client),
    new BatchModerationOptions { MaxBatchSize = 100, MaxBatchDelay = TimeSpan.FromSeconds(30) },
    onError: ex => logger.LogError(ex, "moderation"));

// e.g. inside a BackgroundService:
await worker.RunAsync(stoppingToken);
```

A Claude Message Batches reference classifier lives in
[`samples/Moderation`](../samples/Moderation) (needs the `Anthropic` NuGet package). It runs
`claude-haiku-4-5` with a cached policy prompt and structured output - the cheapest way to
get an LLM verdict per message, at the cost of verdicts arriving in minutes rather than
seconds. That is the right trade for a second line of defense behind `Presend`.

## Redaction

```csharp
var result = await client.RedactMessageAsync(roomId, seq);
// result.RemovedFromHistory, result.RemovedFromStream
```

Calls `DELETE /admin/rooms/{roomId}/messages/{seq}` as `AdminUserId` (default `host-admin`,
preseeded with the `admin` role). dotwire physically erases the message from Postgres and
from the JetStream stream, appends an ids-only audit event, and sends `MessageRetracted`
to live subscribers of the room. It is idempotent: a retry after success is a `200` with both
flags `false`. See ARCHITECTURE.md, "Redaction mechanics".

## Audit, retention, DSAR export

```csharp
var page = await client.ReadAuditLogAsync(afterId: 0, limit: 100);
var verification = await client.VerifyAuditLogAsync(full: true);
var checkpoints = await client.GetAuditCheckpointsAsync();

await client.SetMessageRetentionDaysAsync(365); // 0 = indefinite
var days = await client.GetMessageRetentionDaysAsync();

var export = await client.ExportUserAsync(userId);          // buffered UserExport
await client.ExportUserAsync(userId, someStream);            // streamed straight through
```

## SSE event streaming

```csharp
await foreach (var evt in client.StreamEventsAsync(roomId, asUserId: "user-42"))
{
    switch (evt)
    {
        case MessageEvent m: Console.WriteLine($"{m.SenderId}: {m.Content}"); break;
        case RetractedEvent r: Console.WriteLine($"redacted {r.Seq}"); break;
        case TypingEvent t: break;
        case PresenceEvent p: break;
        case RevokedEvent: return; // membership revoked - server closed the stream, we stop
    }
}
```

By default (`StreamOptions.AutoResume = true`) a dropped connection reconnects with
`Last-Event-ID` set to the last seen message seq, using exponential backoff (1s, capped at
30s) and a freshly minted token per attempt - until cancelled or a `RevokedEvent` is
yielded, which never triggers a reconnect.

## Errors

`DotwireApiException` carries `Kind` (`BadRequest`, `Unauthorized`, `Forbidden`, `NotFound`,
`PresendRejected`, `RateLimited`, `Unavailable`, `Unknown`), the wire `ErrorCode`/`Reason`
(the response body's `error`/`reason` fields, when present), and `RetryAfter` (parsed from
the `Retry-After` header on a `429`).
