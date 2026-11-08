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
