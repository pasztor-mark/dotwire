# Dotwire.Host (.NET)

For coding agents adding dotwire to an application, see the [SDK integration guide](https://github.com/pasztor-mark/dotwire/blob/main/docs/SDK_AGENT_GUIDE.md).

Full documentation: [https://dotwire.pasztormark.dev/docs](https://dotwire.pasztormark.dev/docs)

Host-side SDK: mints RS256 tokens with the host's private key and talks to dotwire over
HTTP. Every message reaches dotwire *through the host backend*, which is what makes the
moderation hooks below both cheap and complete. Targets `net8.0`, `net9.0` and `net10.0`.

```bash
dotnet add package Dotwire.Host
```

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
- Audit read/verify/checkpoints are minted as `AuditorUserId` (default `host-auditor`,
  preseeded by migration 0007 with the `auditor` role).
- Send-as-user / history-as-user / SSE-as-user / `HandleClientSendAsync` mint as that user
  id and resolve role automatically: try `member` first, and on a `403` look the role up via
  `GET /admin/users/{userId}/role`, cache it for `RoleCacheTtl`, and retry once. A second
  `403` surfaces as `DotwireApiException` with `Kind == Forbidden`.
- `MintToken(userId, role, ...)` bypasses resolution entirely - you assert the role.

Admin and auditor tokens are cached internally and re-minted one minute before their own
expiry, so calling admin/auditor-scoped methods in a hot loop doesn't re-mint a token per call.

## System messages

```csharp
var ack = await client.SendSystemMessageAsync(roomId, "system announcement");
var botAck = await client.SendSystemMessageAsync(roomId, "build finished", senderId: "bot:ci");
```

Calls `POST /admin/rooms/{roomId}/messages` as `AdminUserId`. The sender id defaults to
`system`; the admin doesn't need to be a room member, and every injection is audited. If the
audit event can't be written, dotwire erases the message before anyone sees it and the call
throws `DotwireApiException` with `Kind == Unavailable`, so retrying is safe.

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

When the server runs with `Dotwire:HubSend:Enabled=true`, `POST /rooms/{roomId}/messages`
shares one per-user send bucket with the hub `Send` method. The bucket is checked before the
role and membership checks (a malformed body still gets `400` first), and an exhausted bucket
returns `429` with `Retry-After`, surfaced as `DotwireApiException` with `Kind ==
DotwireErrorKind.RateLimited` and `RetryAfter` set. Other errors keep their mapping: `403`
`Forbidden`, `422` `PresendRejected`, `503` `Unavailable`. `SendAsUserAsync` never retries a
`429` itself, and `Idempotency-Key` semantics are unchanged. This SDK has no hub transport:
hub sending is member-facing and lives in `@dotwire/client` (`hubSend: true`), as with
`Typing`.

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
handlers. `DotwireHostOptions.WebhookSecret` (the same value as the server's
`Dotwire:Webhooks:Secret`) is required: the handlers throw without it unless you set
`AllowUnsignedWebhooks = true`. They verify `X-Dotwire-Signature-V2` (HMAC-SHA256 of
`"<X-Dotwire-Timestamp>.<raw body>"`), reject calls older than `WebhookTimestampTolerance`
(default five minutes) and bodies larger than `MaxWebhookBodyBytes` (default 1 MiB, `413`).
The body-only `X-Dotwire-Signature` is accepted only with `AllowLegacyWebhookSignatures = true`.

```csharp
static WebhookHeaders Headers(HttpRequest req) => new(
    req.Headers["X-Dotwire-Signature"], req.Headers["X-Dotwire-Timestamp"], req.Headers["X-Dotwire-Signature-V2"]);

app.MapPost("/webhooks/presend", async (HttpRequest req) =>
{
    var result = await client.HandlePresendWebhookAsync(req.Body, Headers(req));
    return Results.Text(result.Json, "application/json", statusCode: result.StatusCode);
});

app.MapPost("/webhooks/postsend", async (HttpRequest req) =>
{
    var result = await client.HandlePostSendWebhookAsync(req.Body, Headers(req));
    return Results.Text(result.Json, "application/json", statusCode: result.StatusCode);
});
```

### Erasure notices

`HandleErasureWebhookAsync` verifies and parses dotwire's erasure notices, using the same signature
check, replay window and body cap as the postsend handler. Set `Erasure` to apply them. Delivery is
at least once, so the handler must be idempotent. If `Erasure` throws, the handler answers `500`, and
dotwire retries the notice. Notices carry ids only and never content.

```csharp
client.Erasure = async (notice, ct) =>
{
    // message.redacted: RoomId and Seq name the message.
    // user.erased, Status "partial": apply notice.Mode to notice.Messages (delete, or set the sender to dotwire:erased).
    // user.erased, Status "completed": drop any remaining non-message data for notice.UserId.
    await myStore.ApplyErasureAsync(notice, ct);
};

app.MapPost("/webhooks/erasure", async (HttpRequest req) =>
{
    var result = await client.HandleErasureWebhookAsync(req.Body, Headers(req));
    return Results.Text(result.Json, "application/json", statusCode: result.StatusCode);
});
```

Do not re-derive hold or keep rules in the receiver. dotwire applied them before it listed a message.

### Batched asynchronous review

`BatchModerationWorker` drains the queue in size-or-time bounded batches, asks an
`IModerationClassifier` for verdicts, and runs an action for each flagged one - by default,
physical redaction through the admin API, retrying up to three times on a `429` and honoring
the server's `Retry-After`:

```csharp
IModerationClassifier classifier = yourClassifier; // your IModerationClassifier implementation
var worker = new BatchModerationWorker(
    queue,
    classifier,
    onFlagged: BatchModerationWorker.RedactWith(client),
    new BatchModerationOptions { MaxBatchSize = 100, MaxBatchDelay = TimeSpan.FromSeconds(30) },
    onError: ex => logger.LogError(ex, "moderation"));

// e.g. inside a BackgroundService:
await worker.RunAsync(stoppingToken);
```

The classifier is yours to implement: any `IModerationClassifier` that returns a verdict per
message works, and the batching, retry and redaction plumbing above stays the same. Batched
verdicts arrive in minutes rather than seconds, which is the right trade for a second line of
defense behind `Presend`.

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

A redaction that a legal hold or a setting refuses throws `DotwireApiException` with
`Kind == Conflict` (`legal_hold`, `minimum_keep` or `changes_not_allowed`). Nothing is deleted, and no
audit row is written.

## Compliance, holds and erasure

```csharp
var settings = await client.GetComplianceSettingsAsync();
// AllowChanges, MinimumKeepDays, ErasureMode, ActiveHolds, RetentionPaused

var hold = await client.PlaceRoomLegalHoldAsync(roomId, "matter-2026-114");
await client.ReleaseLegalHoldAsync(hold.Id);
var active = await client.ListLegalHoldsAsync(activeOnly: true);
```

`matterRef` is stored in plaintext, so pass an opaque reference, never a name or a description. A room
hold covers every message in that room, and a user hold covers every message that user sent. While any
hold is active, retention is paused (`RetentionPaused`). Releasing a hold twice answers `409`.

```csharp
var started = await client.EraseUserAsync("user-42", ErasureMode.Anonymize); // 202
var status = await client.GetErasureAsync(started.Id);
// status.Status: "in_progress", "waiting" (status.BlockedBy names legal_hold, minimum_keep or
// changes_not_allowed), or "completed" (status.CompletedAt is set)
```

Erasure revokes the user's room memberships and role at once, as `DeleteUserRoleAsync` does. The
messages are handled in the background: `Anonymize` re-attributes them to `dotwire:erased`, and `Delete`
removes them and leaves tombstones. Holds, minimum keep and write-once settings still apply, so a held
message keeps the erasure `waiting`. Erasing the caller's own id, or the last admin, is refused with
`Conflict`. While an erasure is open, role changes and member adds for that user are refused with
`Conflict` (`erasure_open`). Use `ListUserErasuresAsync(userId)` if you lost the erasure id. Erasure does
not delete the identity from your own user table; that is your job.

```csharp
var result = await client.EmergencyDeleteMessageAsync(roomId, seq, EmergencyDeletionReason.CourtOrder);
// or: await room.EmergencyDeleteAsync(seq, EmergencyDeletionReason.IllegalContent);
```

The reason is required and is recorded as a code only (`IllegalContent` or `CourtOrder`), because free
text could carry personal data into the audit log. Emergency deletion bypasses holds, minimum keep and
write-once. It is audited (`message.redacted.emergency`) before the delete. There is no second approver.

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

### Archiving and exporting

```csharp
await using var archive = File.Create("audit.ndjson");
var last = await client.ArchiveAuditLogAsync(archive, afterId: 0);
// later: ArchiveAuditLogAsync(archive, last.LastId, last.LastHash), since previousHash is required when afterId > 0
```

`ArchiveAuditLogAsync` pages the audit log to NDJSON. It recomputes each hash from its predecessor and
throws `InvalidDataException` at the first break. It also writes the checkpoints. Every page emits an
`audit.read` event, as any audit read does.

```csharp
await using var export = File.Create("room-history.ndjson");
var count = await client.ExportRoomHistoryAsync(roomId, "host-export", export);
```

`ExportRoomHistoryAsync` reads history as the named user, so that user must be a room member. Add a
dedicated export member through `AddRoomMembersAsync` (audited as `member.added` and visible to
participants), and remove it afterwards. The export pages from the oldest message and writes one
`HistoryMessage` per line.

## SSE event streaming

```csharp
await foreach (var evt in client.StreamEventsAsync(roomId, asUserId: "user-42"))
{
    switch (evt)
    {
        case MessageEvent m: Console.WriteLine($"{m.SenderId}: {m.Content}"); break;
        case RetractedEvent r: Console.WriteLine($"redacted {r.Seq}"); break;
        case ErasedEvent e: Console.WriteLine($"erased {e.Mode}: {string.Join(',', e.Seqs)}"); break;
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
`Conflict`, `PresendRejected`, `RateLimited`, `Unavailable`, `Unknown`), the wire `ErrorCode`/`Reason`
(the response body's `error`/`reason` fields, when present), and `RetryAfter` (parsed from
the `Retry-After` header on a `429`). A `409` is `Conflict`, with codes such as `legal_hold`,
`minimum_keep`, `changes_not_allowed`, `already_released`, `erasure_open`, `self_erasure` and
`last_admin`.
