# v0.6.0

One technically breaking edge: the reserved id `dotwire:erased`. Other changes are additive, except
that existing 409 responses now classify as `Conflict` in the SDKs (see the table), and the access-log
rows add required members to two SDK types. Migrations `0011` and `0012` run on upgrade, so take a
Postgres backup first and roll the migrating node first.

| Area | Behavior | What to do |
|---|---|---|
| Database | Migration `0011_compliance_holds_erasure.sql` adds the legal hold, erasure, erasure-revocation and erasure-completion tables (append-only, with guard triggers) and the functions `redact_message_checked`, `reattribute_message`, `sync_retention_schedule`, `get_retention_paused` and `retention_job_running`. `set_message_retention` is replaced. | Back up Postgres. Roll the migrating node first. |
| Reserved id `dotwire:erased` | Erasure writes this constant as the sender of anonymized messages. `IdValidation` now rejects it as a token `sub`, in admin path ids and as an injection `senderId`. **Technically breaking:** a user or sender whose id is literally `dotwire:erased` is rejected from now on. No migration renames existing rows. | Search your user ids for this exact value before upgrading. Rename such ids in your host, or accept that they stop working. |
| Redaction refusals | `DELETE /admin/rooms/{roomId}/messages/{seq}` answers `409 legal_hold`, `409 minimum_keep` or `409 changes_not_allowed` when a hold or setting applies. A refusal writes no audit row. Other responses are unchanged. | Handle the 409 codes in admin tooling. Emergency deletion (`POST .../emergency-delete`) is new and bypasses all three. |
| Retention | `GET /admin/retention` adds `paused: bool`. While a hold is active, retention is paused, and the pause survives restarts and policy changes. `PUT /admin/retention` answers `409 minimum_keep` for `0 < days < Dotwire:Compliance:MinimumKeepDays`. Startup fails if `Dotwire:Retention:MessagesDays` is above `0` and below that value. | Read `paused` if you show retention status. Set retention with holds in mind. |
| Error kind `Conflict` | Both host SDKs gain a `Conflict` error kind. Every existing 409 (`self_role_change`, `last_admin`, `already_released`, and the new codes) now classifies as `Conflict` instead of `Unknown`. In .NET, `Conflict` is inserted before `PresendRejected`, so the numeric values of `PresendRejected`, `RateLimited`, `Unavailable` and `Unknown` shift by one. Compare by name, not by number. | Update any `switch` or lookup that matched `Unknown` for 409s, or compared the numeric enum values. |
| Members POST lock | `POST /rooms/{id}/members` now takes the role-mutation lock, so it serializes with role changes and erasure. It answers `409 erasure_open` for a user with an open erasure, as `PUT /admin/users/{id}/role` does. `POST /admin/rooms/{id}/messages` answers the same when `senderId` has an open erasure. | Handle `erasure_open`. Member adds may wait briefly during concurrent role changes. |
| Account erasure and holds | New admin endpoints: `POST /admin/users/{id}/erase`, `GET /admin/erasures/{id}`, `GET /admin/users/{id}/erasures`, `POST /admin/holds`, `DELETE /admin/holds/{id}`, `GET /admin/holds` and `GET /admin/compliance`. Nothing runs unless an admin calls them. An erase request revokes the user's access at once, the same way role deletion does. Anonymize and delete run in the background and are idempotent. | Decide your erasure mode and retention with counsel. Settings are under `Dotwire:Compliance` (README, "Compliance settings"). |
| Erasure webhook (optional) | Set `Dotwire:Webhooks:Erasure:Url` to receive erasure notices. Delivery is **at least once**, so receivers must dedupe and be idempotent. A notice that is not delivered within `Nats:ErasureNoticesMaxAgeHours` (168 by default) expires, and JetStream drops it silently. Startup warns when `PostSend:Url` is set without `Erasure:Url`. | Set the URL, handle notices in your receiver (`onErasure` / `Erasure`), and alert on `dotwire.webhook.erasure.failed`. |
| TimescaleDB 2.32 | The erasure and DSAR sender scans rely on a bloom filter that TimescaleDB 2.32 builds for `sender_id` on compressed chunks. Behavior on 2.11 to 2.31 is not verified for this release. Each erasure update on a compressed chunk decompresses one batch (about 1.3 ms per row in our probe), and the row stays uncompressed until the policy recompresses the chunk. | Use the compose-pinned TimescaleDB 2.32.0, or test your version before relying on erasure throughput. |
| Database (access log) | Migration `0012_access_log.sql` adds the `access_log` hypertable (append-only, with guard triggers), its indexes and `set_access_log_retention`. It replaces `sync_retention_schedule`, `retention_job_running` and `get_retention_paused`, so a legal hold also pauses access-log retention, and `paused` (`/admin/retention`) and `retentionPaused` (`/admin/compliance`) report a held access-log retention job too. The migration always runs; the flag below only gates writes. | Back up Postgres. Roll the migrating node first. |
| Access log (off by default) | `Dotwire:AccessLog:Enabled` defaults to `false`, so nothing is recorded and nothing changes for members. When on, each live window (hub subscribe or SSE stream) and each history page is recorded before it is served, and an open or read the log cannot record is refused: `503 access_log_unavailable` on REST and SSE, `AccessLogUnavailable` on the hub. With NATS unreachable the refusal comes after a 3 s publish timeout; REST history first waits up to 3 s for the JetStream tail, so it answers `503` after about 6 s. New `GET /audit/access` for auditors. `Dotwire:AccessLog:RetentionDays` defaults to `0`, which keeps rows. | Leave it off, or turn it on knowingly. It needs NATS on and `Auth:MaxTokenLifetimeMinutes` above `0`, and must be the same on every node. Record the choice in your risk analysis (COMPLIANCE.md). |
| History during a NATS outage | `GET /rooms/{id}/messages` and SSE replay wait at most 3 s for the JetStream tail top-up, then serve Postgres rows alone; they used to wait for NATS to return. A page can miss messages acked in the last writer batch interval until NATS is back. With the access log on, the read is still refused with `503 access_log_unavailable`, because its record cannot be published. | Nothing. |
| Startup refusals (access log) | With `Dotwire:AccessLog:Enabled=true`, startup refuses `Nats:Enabled=false` and `Auth:MaxTokenLifetimeMinutes=0`. `Dotwire:AccessLog:RetentionDays` must be `0..36500`, and above `0` it must be at least `Dotwire:Compliance:MinimumKeepDays`. | Check these keys before you turn the log on or set `RetentionDays`. |
| Token claim `dw:actor` | New optional claim (AUTH.md), validated like `sub`; a token with an invalid value is rejected. Both host SDKs set it on their own member calls (history, send, participants, SSE), to `AdminUserId` / `adminUserId`, so access-log rows name the host rather than the member. Public mints (`MintToken`, `mintToken`, `tokenResponse`) do not set it unless you pass `actor` (`tokenResponse` takes no `actor`). `IDotwireHostClient.MintToken` gains a trailing optional `actor` parameter: callers compile unchanged, and a class that implements the interface itself must add the parameter. | Nothing, unless you mint tokens yourself and want reads attributed. Then set `dw:actor` on those tokens. |
| Admin responses gain fields | `GET /admin/compliance` adds `accessLogEnabled` and `accessLogRetentionDays`. The DSAR export (`GET /admin/users/{id}/export`) adds an `accessLog` array of the user's access-log rows, where the user is the subject or the actor. | Nothing for JSON consumers. |
| SDK types gain required members | In both host SDKs, `UserExport` gains `AccessLog` / `accessLog` and `ComplianceSettings` gains `AccessLogEnabled` and `AccessLogRetentionDays` (`accessLogEnabled`, `accessLogRetentionDays`). These are required members: code that constructs these records or types by hand (in tests or fixtures, for example) must pass them, which is a source-level break. A missing `accessLog` in an export from an older server parses as an empty list. | Pass the new members wherever you construct these types. |

# v0.5.1

No breaking changes. Disk usage is now tunable: compression of old messages is available and off
by default (`Dotwire__Compression__AfterDays`). The only database change on upgrade is migration
`0010`, which drops the `messages_time_idx` index for every deployment.

| Area | Behavior | What to do |
|---|---|---|
| Index drop | Migration `0010_drop_time_index.sql` runs `DROP INDEX IF EXISTS messages_time_idx`, once, at upgrade. It rebuilds nothing, but takes a brief ACCESS EXCLUSIVE lock on `messages` that blocks reads and writes while held. | Plan a brief pause in reads and writes for the upgrade startup. No other action needed. |
| Compression (opt-in) | `Dotwire__Compression__AfterDays` (unset by default) adds a TimescaleDB compression policy to `messages`, compressing chunks older than N days. `0` removes the policy. Unset runs no compression SQL. Values must be `0` to `36500`, and `AfterDays * 24` must exceed `Nats:RoomsMaxAgeHours` (48 by default). See README, "Recommended settings by scenario." | Nothing, unless you want smaller history on disk. Measured 2.9x smaller at a 100-byte body on TimescaleDB 2.31. |
| First run | The first policy run compresses the whole eligible backlog in one go, which causes heavy I/O. | Enable it in a quiet window, or on a database with spare I/O headroom. |
| Build and version | Compression needs the Timescale License (TSL) and TimescaleDB 2.11 or later, for example the `timescale/timescaledb` image. Apache-only builds (Azure Database for PostgreSQL, `timescaledb-2-oss`) and older versions log a warning naming the reason and stay uncompressed. Our reading of the TSL is that it allows free self-hosting but not offering TimescaleDB itself as a hosted service. Confirm against TimescaleDB's license terms before any hosted offering. | Use the `timescale/timescaledb` image or a self-hosted TSL build, or leave the setting unset. |
| Decompressing | Setting `0` stops new chunks from compressing. Chunks already compressed stay compressed until you run `decompress_chunk` by hand. | Run `decompress_chunk` per chunk if you need the uncompressed layout back. |
| Redaction, DSAR, history | Redaction still removes rows on compressed chunks, and the redaction tombstone still blocks re-inserts. History pages stay index-backed. On TimescaleDB 2.32 a bloom filter skips most compressed batches in the DSAR export's sender-based scan. Older versions may read every compressed batch, which makes DSAR exports slower over compressed history. | Expect slower DSAR exports on compressed databases running TimescaleDB older than 2.32. See COMPLIANCE.md. |
| Dependencies | The compose files now pin NATS 2.15.3 and TimescaleDB 2.32.0 (`timescale/timescaledb:2.32.0-pg17`). The server uses `NATS.Net` 3.4.1, the host SDK uses `jose` 6.2.18, and CI uses `actions/setup-dotnet` 6.1.0. | Nothing if you use the compose files. If you run your own NATS or TimescaleDB, upgrading them is optional. The compression measurements above are from 2.31. |

# v0.5.0

No breaking changes and no database migrations.

| Area | Behavior | What to do |
|---|---|---|
| Metrics | OpenTelemetry metrics are pushed over OTLP and on by default. The exporter uses `OTEL_EXPORTER_OTLP_ENDPOINT`, which compose files set to `http://localhost:4317` unless overridden. `Dotwire__DisableMetrics=true` turns metrics off. No ids or content are exported. See README, "Metrics." | Set `OTEL_EXPORTER_OTLP_ENDPOINT` to your collector in compose, or set `Dotwire__DisableMetrics=true` if you do not run one. |
| Diagnostic metrics | New instruments for the send path (`dotwire.message.send.duration`, `dotwire.jetstream.publish.duration`), fanout (`dotwire.fanout.lag`, `dotwire.fanout.dropped`) and auth lookups (`dotwire.auth.*`). Npgsql pool metrics now carry `db.client.connection.pool.name` = `read` or `write`. See README, "Metrics." | Nothing. Dashboards that summed `db.client.connection.*` across pools still work. |
| Tracing | Opt-in OpenTelemetry traces over OTLP, off by default. `Dotwire__EnableTracing=true` turns them on. Spans are scrubbed to an allowlist of tags, with no paths, SQL text, ids or content. See README, "Tracing." | Nothing, unless you want traces. |
| Local dashboard | New optional `compose.observability.yaml` overlay runs Grafana with Prometheus and Tempo and a provisioned dotwire dashboard (`observability/grafana/dotwire.json`). See README, "Local dashboard (Grafana)." | Nothing. |
| Audit writer | An idle active audit writer runs `SELECT 1` on its locked Postgres session every 30 s, so a dead session is noticed and the advisory lock re-contested sooner. | Nothing. |

# v0.4.3

No breaking changes and no database migrations.

| Area | Behavior | What to do |
|---|---|---|
| AUDIT stream | Switches from limits to interest retention on startup: events leave JetStream once written to Postgres, freeing the space they held. `Nats:AuditMaxBytes` (default 1 GiB) caps the unwritten backlog; at the cap audited admin calls answer `503 audit_unavailable` instead of dropping events. | Nothing. Keep the `audit-writer` consumer; make any extra `AUDIT` consumer ack. |
| Health check | `/healthz` is exempt from rate limiting, so a flood of unauthenticated traffic through a proxy can no longer fail the proxy's health checks. | Nothing. |
| SDK | `global.json` requires SDK `10.0.403` or a later `10.0` feature band; older SDKs ship a Native AOT compiler with GHSA-p3jp-9v83-76w9. | Install a current .NET 10 SDK before building. |

# Upgrading to 0.4.0

Applies to upgrading from 0.3.x. No breaking changes and no database migrations. 0.3 SDKs work
against a 0.4 server, and a 0.4 `@dotwire/client` with `hubSend: true` falls back to REST against
a 0.3 server.

| Area | 0.4.0 behavior | What to do |
|---|---|---|
| Hub sends | `Dotwire:HubSend:Enabled` (default `false`) adds the hub `Send(roomId, content, idempotencyKey)` method, returning `{ seq, time }`. Same validation, presend/postsend and idempotency as REST. | Optional: enable on `All`/`Gateway` nodes, then set `hubSend: true` in `@dotwire/client`. |
| Send rate limit | With hub sends on, REST sends and hub `Send` share one per-user `send` bucket per node. | Raise `RateLimits:Send` if clients use both paths at once. |
| Hub send authorization | Authorized by the live subscription: revocations apply at once, role changes at the next `Dotwire:MembershipSweepSeconds` sweep. REST still checks per request by default. `Dotwire:AuthBatching:Enabled` (default `false`) batches those lookups without changing decisions. `Dotwire:AuthCacheSeconds` (default `0`, max `60`) opts REST room routes into a bounded cache of allowed decisions, which widens the REST revocation window. | Lower the sweep interval if role changes must apply faster. Leave `AuthCacheSeconds` at `0` unless that window is acceptable. |
| Gateways | With hub sends on, gateways publish to JetStream and call the presend/postsend webhooks. | Let gateways reach your webhook URLs. |
| Read pool | `compose.yaml` sets `Minimum Pool Size=20;Maximum Pool Size=100` on `PostgresRead`; each node opens the minimum on startup (5 s budget, warning on shortfall). | Size pools for your node count against Postgres `max_connections`. |
| Room access | Role cross-check and membership run as one query (`RoomAccessFilter`). Responses unchanged. | Nothing. |
| Auth batching | `Dotwire:AuthBatching:Enabled` (opt-in, default `false`) batches REST room authorization lookups. It helps only when the database read pool is saturated and is slightly slower at light to moderate load; see README, "Performance notes." `Dotwire:AuthBatching:MaxInFlight` (default `2`, range `1..64`) caps concurrent batched auth queries per node. `Dotwire:AuthBatching:MaxBatchSize` (default `512`, range `1..4096`) caps lookups per batched query. | Nothing. Set `Enabled` to `true` to opt in, ideally after benchmarking. Out-of-range values fail startup. |
| REST decision cache | `Dotwire:AuthCacheSeconds` (default `0`, range `0..60`) enables the opt-in REST decision cache when above `0`. Out-of-range values fail startup. See AUTH.md, "Threat notes." | Leave at `0` unless the wider revocation window is acceptable. |
| Logging switches | `Dotwire:DisableDebugLogging` (default `false`) raises the default log level to Warning, dropping Trace, Debug and Information. `Dotwire:DisableAllLogging` (default `false`) turns all logging off, including warnings and errors, and wins over `DisableDebugLogging`. Blank values in `.env` fall back to the defaults. | Nothing. Set one to `true` if logging shows up in your CPU profile. |

Rollout: roll servers with hub sends off, then SDKs, then enable `Dotwire:HubSend:Enabled` on every
`All`/`Gateway` node and ship clients with `hubSend: true`. Roll back by unsetting the flag.

# Upgrading to 0.3.0

Applies to upgrading from 0.2.x. The server image, `Dotwire.Host` and `@dotwire/client`,
`@dotwire/host` and `@dotwire/react` all move to `0.3.0` together; mixing a 0.2 SDK with a 0.3
server is not supported.

## Breaking changes

| Area | 0.3.0 behavior | What to do |
|---|---|---|
| Webhook signing | Host SDK webhook handlers throw unless `WebhookSecret` / `webhookSecret` is set. Calls are verified with `X-Dotwire-Signature-V2` (HMAC of `"<X-Dotwire-Timestamp>.<body>"`), reject timestamps older than five minutes and bodies over 1 MiB. | Set the secret on both sides, or opt out with `AllowUnsignedWebhooks = true` / `allowUnsignedWebhooks: true`. Pass the new headers to the handlers (see the SDK READMEs). The body-only `X-Dotwire-Signature` needs `AllowLegacyWebhookSignatures` / `allowLegacyWebhookSignatures`. In .NET, the old `HandlePresendWebhookAsync(body, signatureHeader)` overload still compiles but answers `401` to every call: switch to the `WebhookHeaders` overload. |
| Webhook URLs | Server refuses `http` webhook URLs except loopback. Post-send queue default is 1000 items and 64 MiB (was 10000). | Use `https`, or set `Dotwire:Webhooks:AllowInsecureUrls=true`. Raise `QueueCapacity` / `MaxQueueBytes` if you relied on the old depth. |
| Token lifetime | `iat` is required, must not be in the future, and `exp - iat` may not exceed `Auth:MaxTokenLifetimeMinutes` (default 60). | Mint shorter tokens, or raise the cap (`0` disables it). |
| Keys | `Auth:Keys` must be public-key PEM of at least 2048-bit RSA; a private key fails startup. JWKS URLs must be `https` unless `Auth:AllowInsecureJwksUrl=true`. | Check your key config before rolling. |
| Query-string tokens | `?access_token=` is accepted only on `GET /hub/rooms` and `GET /rooms/{uuid}/events`, and only when no `Authorization` header is present. | Send tokens in the `Authorization` header everywhere else. |
| CORS | 0.2 reflected any origin and allowed credentials. 0.3 allows `Cors:AllowedOrigins` (any origin when empty) and never allows credentials. | The default `@dotwire/client` transport (`'websockets'`) is unaffected. With `transport: 'negotiate'` on a cross-origin page, the negotiate request carries credentials and is blocked; switch back to `'websockets'`. Set `Cors:AllowedOrigins` before exposing dotwire. |
| Role changes | Admins cannot change their own role (`409 self_role_change`) or remove the last admin (`409 last_admin`). | Update admin tooling that did either. |
| Compose | Port 8080 binds to `127.0.0.1`. The multinode stack needs `NATS_USER`, `NATS_PASSWORD`, `POSTGRES_REPLICATION_PASSWORD` and `HAPROXY_STATS_PASSWORD` in `.env`. Images are pinned (`DOTWIRE_VERSION`, default `0.3.0`). | Front it with TLS before exposing it; fill in `.env`. |
| Node | The npm packages require Node 22 or newer (was 20). | Upgrade your runtime. |
| Room ids | `@dotwire/host` throws a `TypeError` on a room id that is not a UUID. | Use UUIDs. |
| `IDotwireHostClient` | Gains `SendAsUserAsync(..., idempotencyKey, ct)` and the `WebhookHeaders` overloads of both webhook handlers. | Add them to any fake or custom implementation. |

Already true in 0.2.1, so not a change: the hub is WebSockets-only, and hub and SSE connections
close when the token expires.

## Non-breaking, but new defaults to know about

- Limits: 256 KiB request body (`413` above it), 100 hub plus SSE connections per user (a hub
  connection is refused and SSE answers `429` above it), 1000 SSE replay rows per resume, and a
  per-IP bucket for unauthenticated requests (`RateLimits:Unauthenticated`).
- Hub connects now count against the `read` rate-limit bucket.
- The `hub` bucket (Subscribe/Unsubscribe) is keyed per user instead of per connection, so a
  user with many tabs shares one budget. Raise `RateLimits:Hub` if clients subscribe to many
  rooms at once.
- `Dotwire:MembershipSweepSeconds` (default 60) re-checks live subscriptions against current roles
  and membership.
- New messages use encryption envelope v1 (HKDF-derived per-message key, associated data bound to
  room, sender and key id). Older envelopes still decrypt while
  `Dotwire:Encryption:AllowLegacyEnvelopes` is `true` (the default); set it to `false` once every
  pre-0.3 row has aged out of Postgres and JetStream.
- Sends accept an optional idempotency key (`Idempotency-Key` header, `idempotencyKey` in the SDKs).

## Database

Migration `0009_audit_integrity.sql` runs on the migrating node at startup. It adds a unique index on `audit_log.prev_hash`, a `stream_time` column and a checkpoint validation trigger. Take a backup first and roll the migrating node before
the rest.

## Rollout order

1. Back up Postgres.
2. Update `.env` and config for the table above.
3. Roll the migrating node, then the remaining nodes.
4. Roll host applications with the 0.3.0 SDKs, with webhook secrets set.
5. Roll browser clients.

Normative detail lives in [AUTH.md](AUTH.md), [ARCHITECTURE.md](ARCHITECTURE.md) and
[COMPLIANCE.md](COMPLIANCE.md).
