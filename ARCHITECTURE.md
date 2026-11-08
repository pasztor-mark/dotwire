# dotwire — Architecture

## Stack

dotwire is .NET 10 on ASP.NET Core, built with Native AOT (`CreateSlimBuilder` plus
source-generated JSON contexts) — the whole service ships as one binary with fast startup and
a small memory footprint, which matters directly for the connection-density numbers in the
scale contract. Realtime delivery runs on SignalR, chosen over a hand-rolled WebSocket layer
for its reconnect and group-management primitives, but AOT imposes real constraints that
agents must not "fix": only the JSON hub protocol is available (no MessagePack), and there is
no strongly-typed `Hub<T>` — reflection-based dispatch that AOT can't source-generate around.
Persistence is PostgreSQL with the Timescale extension, with messages stored as a hypertable.
Messaging and durability run on NATS with JetStream, which does double duty as both the
fanout bus and the persistence-ack mechanism (see "Write path" below). One binary, one
process model, one config flag selects the node's role — see "Realtime and node roles."

## Write path (JetStream-first)

The gateway validates an incoming send, then publishes it to a per-room subject
`room.{room_id}` on a JetStream stream. **The JetStream persistence ack is the client ack** —
the client is told "sent" the moment JetStream confirms durable receipt, not when the message
lands in Postgres. SignalR fanout consumes off NATS immediately and independently; a durable
consumer batch-inserts messages into Postgres behind that ack path, on its own schedule.

WHY: this decouples send latency from Postgres entirely. Postgres batching never sits in the
critical path of an ack, and Postgres downtime does not stop chat — messages keep flowing
through JetStream and fanout even if the batch-insert consumer is stalled or the database is
down. The costs of that decoupling are accepted deliberately: Postgres can lag live delivery
by roughly one batch interval, so "in Postgres" is not synonymous with "sent," and any
reconnect/replay story has to be built on sequence numbers rather than on Postgres being
current. (The history endpoint hides most of that lag by topping its page up from the
JetStream tail — see "Read path.")

**Ordering token:** the JetStream stream sequence, monotonic within a room (each room has its
own subject on the stream, so ordering is per-room, not global). **Gap-fill contract:** a
reconnecting or lagging client is served "room X, seq > N" — give me everything after the
last sequence I have. This sequence-based gap-fill is the entire replay story; it replaces any
notion of connection-level replay buffering (see "Concurrency and fanout disciplines" —
SignalR's own stateful reconnect buffering is explicitly disabled, precisely because this
contract already covers it).

**Ephemeral traffic rule (contract, not a suggestion):** presence, typing indicators, and read
receipts ride NATS core, never JetStream. They are never persisted and never sit in any ack
path. Agents must not "helpfully" start persisting them — that would silently turn a
constant-cost broadcast mechanism into a storage and replay liability for data nobody needs to
recover.

**The live subject, and why the stream filter is `room.*` and not `room.>`.** Alongside the
durable `room.{room_id}` JetStream subject, each room also has a core (non-JetStream) subject
`room.{room_id}.live` used for seq-tagged live fanout, plus `room.{room_id}.ephemeral.*` for
presence/typing/read-receipts. The `ROOMS` stream's subject filter (`Nats:RoomsSubjectFilter`,
default `room.*`) matches exactly one token after `room.` — it captures `room.{room_id}`
durable traffic and nothing else. It must not be widened to `room.>` (which would match every
token, including `.live` and `.ephemeral.*`): that would pull ephemeral, by-design-unpersisted
traffic onto the JetStream stream, silently violating the ephemeral traffic rule above. This
is why the filter is a narrow single-token match rather than the more permissive wildcard that
might look equivalent at a glance.

**Rejected alternatives** (one line each, so they aren't relitigated):
- *Postgres-first batched writes* — simpler consistency story, but batch linger sits in every
  send path and Postgres becomes a hard dependency for sending at all, not just for history.
- *Transactional outbox* — strongest consistency guarantee available, but lowest throughput of
  the options considered; not worth the cost at this scale.

## Webhooks (presend / postsend)

PRODUCT.md's "AI moderation" out-of-scope carve-out names the presend hook as the designed
extension point; that extension point now has a companion postsend hook and a server-side
webhook call-out mode, not just an in-process SDK delegate. Two independent call-outs exist,
each active only when its URL is configured (`Dotwire:Webhooks:Presend:Url` /
`Dotwire:Webhooks:PostSend:Url` — unset means off, "defaults need nothing set"):

- **Presend** (`Dotwire:Webhooks:Presend`) is synchronous and blocking, called before a
  message is accepted, with its own timeout (`TimeoutMs`, default 500) and a configurable
  fail policy: `Closed` (default — an unreachable/erroring webhook fails the send with `503
  presend_unavailable`) or `Open` (lets the original content through unchanged). It can reject
  the send or rewrite content.
- **Postsend** (`Dotwire:Webhooks:PostSend`) is fire-and-forget from the send path's
  perspective — the message is already accepted and has a `(roomId, seq)` — queued
  (`QueueCapacity`, default 1,000, plus `MaxQueueBytes`, default 64 MiB of queued content; a
  full queue drops the newest item with a warning rather than blocking a send) and delivered with retries (`MaxAttempts`, default 3) and its own timeout
  (`TimeoutMs`, default 2000). It never blocks or affects the send path itself.

When `Dotwire:Webhooks:Secret` is configured, both call-outs carry `X-Dotwire-Timestamp`
(unix seconds), `X-Dotwire-Signature-V2: sha256=<hex HMAC-SHA256 of "<timestamp>.<raw body>">`
and, for older hosts, the legacy `X-Dotwire-Signature: sha256=<hex HMAC-SHA256 of the raw
body>`. The host SDKs verify the timestamped signature and reject calls more than five minutes
old, so a captured webhook call cannot be replayed later; accepting the legacy body-only
signature is an explicit opt-in (`AllowLegacyWebhookSignatures` / `allowLegacyWebhookSignatures`).
The SDK handlers refuse to run without a secret unless `AllowUnsignedWebhooks` /
`allowUnsignedWebhooks` is set, because an unsigned postsend handler wired to moderation lets
anyone trigger redaction of real messages. Webhook URLs must be `https` (or a loopback host)
unless `Dotwire:Webhooks:AllowInsecureUrls=true`; dotwire refuses to start otherwise, and logs
a warning when URLs are configured without a secret. Presend responses are capped at 1 MiB and
the SDK handlers cap incoming bodies at 1 MiB (`MaxWebhookBodyBytes` / `maxWebhookBodyBytes`). `IncludeAdminSends` (both hooks, default `false`) controls whether
admin message injection (see AUTH.md, "Message injection is audited...") also runs through
presend/postsend — off by default, since injected messages are already host-authored and
audited by construction. Both host SDKs (`Dotwire.Host`, `@dotwire/host`) ship matching
webhook-handler methods (`HandlePresendWebhookAsync`/`handlePresendWebhook`,
`HandlePostSendWebhookAsync`/`handlePostSendWebhook`) that verify the signature and run the
same `Presend`/`PostSend` delegates used for direct SDK sends, so one implementation covers
both the "call dotwire directly" and "point dotwire's webhook at my host" integration shapes.

## Realtime and node roles

SignalR runs behind an L4 load balancer. Connections are **WebSockets-only with
`skipNegotiation`** — no long-polling or SSE fallback negotiation — which means there is **no
sticky-session requirement** at the load balancer at all; a dumb TCP proxy is sufficient (see
"Concurrency and fanout disciplines" below for the full deployment posture).

SignalR hubs are the **primary subscribe path**. The one alternative is the SSE read path
(`GET /rooms/{roomId}/events`, see "Read path"), a plain-HTTP consumer for agents and
harnesses that rides the same NATS fanout. Sending does not require holding a socket at all: a **REST send
endpoint exists regardless of SignalR**, specifically so host backends can inject system
messages (bot replies, notifications, moderation actions) without maintaining a live
connection just to publish.

Horizontal scaling of SignalR requires a backplane, and no official NATS backplane exists, so
dotwire ships a custom backplane: a `HubLifetimeManager<T>` implemented over NATS core. It is
**interest-based** — each node subscribes only to the `room.*` subjects for rooms that have at
least one locally connected member, not to the full subject space. That means backplane
traffic scales with local interest (how many rooms this node's own connections care about),
not with the size of the cluster or the total number of rooms in existence — the property that
makes horizontal scale-out genuinely linear rather than degrading as nodes are added.

**Node roles:** one binary, selected at startup by a config flag: `all` (the default — a
single process serves everything, so single-node self-hosting stays a one-compose-file
afternoon project), `api` (REST + admin surface only), or `gateway` (holds sockets, consumes
NATS, and does nothing else). Nodes are stateless regardless of role, so a large deployment can
scale socket capacity (more `gateway` nodes) independently of API/admin load (more `api`
nodes) without changing the software, only the flag.

## Membership revocation and live sockets

Room membership can be revoked while a member holds a live connection. Revocation (via the
admin API) does not wait for the connection to notice on its own: `RoomInterestManager`
tracks, per connection, which rooms it is subscribed to (spec §3.5), so a revocation can find
every locally-connected socket for that `(roomId, userId)` and act immediately — it sends
`MembershipRevoked(RoomId, UserId)` to the affected connection(s), unsubscribes them from the
room's SignalR group, and queues a presence-leave. `@dotwire/client` and `Dotwire.Host`/
`@dotwire/host` both surface this as a first-class event (`onMembershipRevoked` /
`MembershipRevoked`) rather than leaving hosts to infer revocation from a socket just going
quiet. Revocation propagates across a multi-node deployment the same way regular fanout does —
over the NATS-core backplane — so a revoked member is dropped from every node they happen to
be connected to, not just the node that served the admin request.

NATS core is at-most-once, so the broadcast alone is not the guarantee. The admin endpoints
retry the broadcast (three attempts, two seconds each); `Subscribe` and the SSE endpoint
re-check membership after registering the connection, closing the race where a revocation
lands between the check and the registration; and every gateway runs a membership sweep
(`Dotwire:MembershipSweepSeconds`, default 60, `0` disables) that re-validates every live
subscription against `room_members` and `user_roles` in one batched, `room_id`-keyed query and
drops the ones that no longer match. Changing a user's role through `PUT /admin/users/{id}/role`
also revokes their live subscriptions, since their outstanding tokens now carry a stale role.
The worst-case window for a lost broadcast is therefore one sweep interval, not the token TTL.
If the NATS fanout loop for a room dies unexpectedly, the node drops that room's local
subscribers so they reconnect and gap-fill instead of silently going deaf.

## Data layout

Storage is PostgreSQL with the Timescale extension. The `messages` table is a hypertable keyed
on `(room_id, time)`, with the JetStream `seq` stored alongside each row so history rows can be
correlated back to their position in the ordering token from the write path. Two Npgsql data
sources are split in code from day one — a read pool and a write pool — and history reads are
routed to the read pool; this split exists from the start specifically so it never has to be
retrofitted under load. Authorization lookups (`user_roles`, `room_members`) are reads and go
through the read pool too, behind one `IAccessStore` seam. When the read pool points at an
asynchronous replica, a demotion or membership removal takes effect after replica lag; deploy
the read pool against the primary or a synchronous replica if that window matters. **Shard-key discipline:** every hot-path query includes `room_id`.
Sharding itself is out of scope for v1 (see PRODUCT.md's out-of-scope list), but the schema and
query discipline are shard-*ready* by construction — nothing is built that would foreclose
sharding later.

**Authoritative tables:**
- `user_roles` — role per user (`member` | `auditor` | `admin`), preseeded but fully
  configurable; can also be populated via the admin API.
- `room_members` — room membership, host-managed via the admin API. This table, not the JWT, is
  the authoritative record of who belongs to which room.

**Authorization pipeline order**, which AUTH.md treats as normative: **signature →
standard claims (`exp`, `aud`, `iss`) → role cross-check against `user_roles` → membership check
against `room_members`.** The JWT proves identity and a claimed role only; `user_roles` is
authoritative and a mismatch denies. This pipeline order — and the `dw:role` claim it
cross-checks — is the contract AUTH.md builds its normative host-integration language on top
of; it must not drift between the two documents.

## Audit log

Audit events flow through a dedicated JetStream subject, distinct from room traffic. A
**single durable consumer** reads that subject, computes
`hash = SHA-256(prev_hash ‖ canonical_event)` for each event in order, and batch-inserts the
results into an append-only Postgres table.

WHY single-writer: this is what makes hash-chaining and batched writes compatible at all.
Hash-chaining requires a strict total order to compute each link from the previous one;
batching requires grouping writes for throughput. A single ordered consumer gives the chain its
order once, up front, and batching happens downstream of that — with multiple writers, the two
requirements would fight each other.

Append-only is enforced at the database level, not just by convention: the service role has no
UPDATE or DELETE grants on the audit table, and a guard trigger backs that up. A daily
checkpoint anchors the chain so that verifying integrity later doesn't require scanning the
entire history from genesis. Reading the audit log is itself an audited action — it emits its
own audit event, so "who looked at the audit trail" is always answerable from the trail itself.
**Audit events carry message/room/user ids only, never message content** — that invariant (see
"Redaction mechanics" under "Encryption at rest & data lifecycle" for how it's enforced) is
what lets erasure never rewrite an audit entry, so the hash chain survives every redaction
intact.

**Canonical form.** Before hashing, each event is reduced to a fixed, order-stable canonical
form (`AuditCanonicalForm.Compute`) — the same event must always serialize to the same bytes
regardless of how it arrived, or the chain would be unverifiable against independently
reconstructed events. Timestamps are truncated to microsecond precision
(`TruncateToMicroseconds`) before hashing, so Postgres's own timestamp rounding can never
produce a hash mismatch against a value computed elsewhere. The chain itself is
`hash = SHA-256(prev_hash ‖ canonical_event)`; the very first event chains from a fixed genesis
hash (`AuditCanonicalForm.GenesisHash()`) rather than a null or empty predecessor, so link 0 is
verifiable the same way every later link is.

**Checkpoints.** Once per day (tracked by `_latestCheckpointDay`, one checkpoint per calendar
day), the writer inserts a row into the append-only `audit_checkpoints` table
(`(day, last_id, hash)` — same revoked-grants-plus-guard-trigger protection as the audit table
itself) recording the chain's tail id and hash at that point. `GET /audit/verify` can start
verification from the most recent checkpoint at or before the requested range instead of
walking the chain from genesis every time — checkpoints are anchors for cheap partial
verification, not a replacement for the full chain, which remains available and verifiable in
full at any time. Operationally, archiving checkpoints (e.g. exporting `audit_checkpoints`
rows to cold storage on the same cadence as other compliance backups) gives an auditor a
trusted, independently-held anchor to verify against, without depending on the live database
being uncompromised.

**Integrity hardening.** `audit_log.prev_hash` is unique (migration 0009), so two writers that
lost the advisory lock can never fork the chain silently: the second insert fails. A
`BEFORE INSERT` trigger on `audit_checkpoints` rejects checkpoints dated in the future or whose
`(last_id, hash)` does not match the `audit_log` row, and `/audit/verify` re-checks the anchor
row against the checkpoint hash before trusting it (a mismatch reports `ok: false` at the
anchor). Checkpoints dated after today are ignored by both the writer and the verifier. The
writer also records the JetStream publish time in the unhashed `stream_time` column next to
the node-stamped `event_time`, and turns an unparseable stream entry into a hash-chained
`audit.malformed` placeholder instead of skipping it, so `stream_seq` has no silent gaps.
Admin mutations publish their audit event before committing the database change: a failed
commit can leave an audit event for a change that did not happen (a retry then succeeds), which
is the accepted direction — a duplicate or orphaned event is detectable, a missing one is not.
The chain hash is unkeyed SHA-256: anyone with write access to the database can recompute it,
so the real tamper anchor is exporting `audit_checkpoints` to storage the database cannot
write. Default verification starts at the latest checkpoint; run `?full=true` (single-flight
per node) to re-check rows below it.

**Single-writer via Postgres advisory lock.** Exactly one process may run the audit writer at
a time — required for the strict total order hash-chaining depends on (see WHY above) — and
that exclusivity is enforced with a Postgres session-level advisory lock
(`pg_try_advisory_lock`, held for the writer's lifetime on a fixed lock key), not by
application-level coordination or a NATS durable-consumer property alone. A node that fails to
acquire the lock (another node already holds it) goes into standby, retrying acquisition every
`Nats:AuditWriter:StandbyRetrySeconds` (default 5s), so a multi-node deployment always has
exactly one active writer and a hot standby ready to take over if the active writer's
connection drops (which also releases the advisory lock, since it is session-scoped).

## Concurrency and fanout disciplines

In a chat system, ingest is cheap; concurrency and fanout are where systems bleed — most
connections are idle at any given instant, but every one of them still costs memory, and every
message sent fans out to all of them. The rules below are how dotwire spends its discipline
budget on that specific axis. They are **binding design rules, not suggestions**: they are all
free in the sense that they cost no new components, only configuration and code discipline, so
there is no excuse to relax them under schedule pressure.

**Transport / per-connection cost:**
- **WebSockets-only, `skipNegotiation`.** No long-polling or SSE fallback. WHY: eliminates the
  sticky-session requirement at the load balancer entirely, which is what lets a dumb L4/TCP
  proxy sit in front of the cluster.
- **SignalR stateful reconnect buffering disabled.** Reconnect means resubscribe plus
  sequence-based gap-fill (see the write path's ordering token), not a client replaying from a
  server-held buffer. WHY: per-connection replay buffers cost memory per connection held
  indefinitely; the gap-fill contract already provides recovery, so the buffer is pure
  duplicated cost.
- **WebSocket per-message compression (`permessage-deflate`) disabled.** WHY: per-connection
  compression contexts cost memory multiplied by connection count, for negligible gain on
  already-small JSON payloads.
- **Long keep-alive/ping intervals (~30s).** WHY: idle connections are the majority at any
  moment; halving ping frequency halves their chatter with no loss of liveness detection that
  matters at this timescale.

**Per-message / fanout cost:**
- **Authorize at connect, not per message.** Membership is checked against `room_members` once,
  at join time, cached in connection state, and invalidated via membership-change events
  carried on NATS core. WHY: this keeps the database off the send/fanout hot path entirely —
  the expensive check happens once per connection lifetime, not once per message.
- **Serialize once per message, not once per connection.** JSON-only AOT source-generation
  makes a single serialized payload naturally shareable across every fanout target. WHY:
  fanout cost would otherwise scale with connections × messages instead of just messages.
- **Bounded per-connection send queues, disconnect-the-laggard policy.** WHY: one slow client
  must never be able to stall fanout for an entire room; a bounded queue plus disconnection
  caps the blast radius of a single bad connection.
- **Presence coalescing.** Presence transitions per room are debounced server-side into windows
  (roughly one delta every few seconds) rather than emitted per raw transition; typing
  indicators are throttled by the same convention. WHY: presence event volume runs 10–100x
  message volume at scale, so leaving it unthrottled would dwarf actual message traffic.
- **Per-user token-bucket rate limiting**, enforced in-process. WHY: bounds worst-case
  per-user cost without a dependency on an external rate-limiting service. The hub bucket is
  keyed by `sub`, so opening more sockets does not buy more Subscribe calls, and each node caps
  concurrent hub plus SSE connections per user (`Dotwire:MaxConnectionsPerUser`, default 100;
  over the cap a hub connection is refused and SSE answers `429`). Typing is throttled per
  `(user, room)` across connections, and when a connection's send queue is more than half full
  typing and presence events are shed so only durable messages can trigger
  disconnect-the-laggard.
- **Per-route token-bucket rate limits** (`Dotwire:RateLimits`, on by default), keyed per user
  (`sub`) for the `send`, `read` (history, SSE connect, hub connect, `/audit`), `admin` (every
  `/admin` route, including DSAR and retention) and `hub` (Subscribe/Unsubscribe — typing keeps
  its own separate cooldown, unaffected by this bucket) buckets. Requests without a valid token
  share a per-client-IP `Unauthenticated` bucket that runs after authentication and before
  authorization, so bad-token floods are limited too (behind a proxy every client shares the
  proxy's IP; size that bucket accordingly). Request bodies are capped at
  `Dotwire:MaxRequestBodyBytes` (default 256 KiB) before any parsing.
  Each is a standard ASP.NET Core token-bucket limiter (`PermitLimit`/`TokensPerPeriod`/
  `PeriodSeconds`, independently tunable per bucket). WHY: in-process limiting needs no new
  infrastructure component (PRODUCT.md's out-of-scope list rules out Redis) and is enough at
  this scale contract; the documented cost is that with N API nodes behind a load balancer the
  effective limit is N× the configured value, since nodes don't coordinate counters — stated
  plainly here rather than solved with a shared store that would be redundant infrastructure
  for the traffic this system targets.

**Deployment posture (documented defaults, not code):**
- **TLS is mandatory in production.** The bundled compose files and `haproxy.cfg` speak plain
  HTTP on loopback-bound ports for local use only; terminate TLS in front of them (or add a
  `bind ... ssl crt ...` line plus HSTS to HAProxy). Browsers cannot set headers on a WebSocket
  upgrade, so the SignalR client sends its JWT as `?access_token=`; dotwire accepts a query token
  only on `GET /hub/rooms` and `GET /rooms/{uuid}/events`, never when an `Authorization` header is
  present, and the bundled HAProxy rejects `access_token` on every other path. Keep token TTLs
  short and keep URL logging off (or scrubbed) at every proxy.
- **CORS is an allowlist.** Set `Cors:AllowedOrigins` to the host app's origins; with no list
  configured dotwire allows any origin but never sends `Access-Control-Allow-Credentials`
  (authentication is bearer-only). Set `AllowedHosts` to the real host names as well.
- **Gateways hold no owner credentials.** Only the migrating node (or a one-shot migrator job)
  gets `ConnectionStrings:PostgresMigrator`; runtime-only nodes connect as `dotwire_app`.
  Applied migrations are checksummed in `schema_migrations`, and startup fails if an applied
  script was edited.
- **Server GC with DATAS**, plus raised file-descriptor limits and socket backlog, documented
  in deployment docs and set in the compose file. WHY: connection-dense workloads need GC and
  OS limits tuned away from their interactive-workload defaults.
- **L4/TCP passthrough load-balancer guidance.** WHY: WebSockets-only with no sticky sessions
  permits a dumb TCP proxy, which minimizes per-connection proxy overhead compared to an L7
  proxy terminating and inspecting every connection. The bundled `haproxy.cfg` runs in HTTP
  mode only because it serves both node roles from one listener and routes by path (hub and
  SSE to `gateway`, everything else to `api`); give each role its own listener or hostname
  and a pure L4 proxy works.

**Expected effect:** these disciplines move the per-node ceiling from a naive ~50k toward
roughly **100,000–200,000 concurrent connections per node** (PRODUCT.md's scale contract), and
make horizontal scale genuinely linear — the interest-based backplane, stateless nodes, and the
role flag mean adding nodes adds capacity without adding coordination overhead, up to a modest
cluster's roughly 1–2 million concurrent connections and 5,000–10,000 messages per second
sustained.

## What "sent" means

The ack ladder, stated plainly so it isn't relitigated: **client ack = JetStream persisted.**
That is the only guarantee "sent" carries. Fanout to other connected clients is
best-effort-immediate — it happens right after JetStream ack, off the same event, but it is not
itself part of the ack contract. History durability in Postgres lags behind by roughly one
batch interval, per the write path above; a message being "sent" never implies it is already
queryable from Postgres.

**Reconnect story:** a client that reconnects — after a network blip, a redeploy, or simply
falling behind — gap-fills by sequence number ("room X, seq > N"), exactly as described in the
write path's ordering token section. A connection that falls too far behind a room's fanout
(per the bounded-queue, disconnect-the-laggard policy above) is disconnected outright rather
than allowed to stall the room, and recovers through that same gap-fill path on reconnect —
there is exactly one recovery mechanism, not a special case for laggards versus a special case
for network blips.

## Read path

Reading is two distinct paths, not one, and the distinction matters. **History reads** —
fetching past messages — go against Postgres via the dedicated read pool (see "Data layout"),
never the write pool. Because Postgres trails the JetStream ack by up to one batch interval,
the newest page and `afterSeq` pages are topped up from the `ROOMS` stream's tail (redaction
tombstones still apply), so a just-acked message is normally readable at once; if that
JetStream read fails, the endpoint serves Postgres rows alone. **Live delivery** — messages as they're sent — comes through SignalR
fanout off NATS, not through Postgres at all; a connected client never polls the database for
new messages. The two paths reconnect at gap-fill: a client rejoining after a blip or falling
behind is served "room X, seq > N" (see the write path's ordering token section), which itself
may be satisfied from either path depending on how far back the gap runs. Content is decrypted
at the point of delivery in both cases — at the fanout edge for live messages, on the history
read itself for stored ones — per "Encryption at rest & data lifecycle" below; neither Postgres
nor the NATS file store ever hands back plaintext. SignalR hubs are the primary way to receive
live delivery; history reads, by contrast, are ordinary REST calls and don't require a socket.

**SSE read path.** `GET /rooms/{roomId}/events` (`Dotwire.Api.Events`, gated the same as any
room-scoped route: authenticated, role-cross-checked, membership-checked, rate-limited under
the `read` bucket) is a second, zero-dependency live-delivery path alongside SignalR, aimed at
agents and harnesses that want plain HTTP rather than a full SignalR client (PRODUCT.md's
"who it's for" — agents are a supported secondary audience). It streams
`text/event-stream`, replaying history via `afterSeq` when given one (paged at
`Dotwire:Sse:ReplayPageSize`, default 100) before switching to live delivery off the same NATS
fanout SignalR uses, so ordering and gap-fill semantics match the SignalR path exactly — an
SSE consumer is not a second, divergent notion of "what happened in this room." Each connection
gets a bounded channel (`Dotwire:Sse:BufferSize`, default 256); a laggard consumer that fills
it is disconnected, mirroring the bounded-queue/disconnect-the-laggard fanout discipline below.
A resume replays at most `Dotwire:Sse:MaxReplayRows` (default 1000) messages per connection;
past that the server ends the stream and the client resumes with `Last-Event-ID`, so each
replay chunk is paid for by a rate-limited request. Rows that cannot be decrypted are skipped
without stalling the replay cursor. The JetStream tail top-up reads at most twice the page size
per request.
Periodic keep-alive comments (`Dotwire:Sse:KeepAliveSeconds`, default 15) keep idle connections
from being reaped by intermediate proxies. Runs on the Gateway node role; toggled off entirely
via `Dotwire:Sse:Enabled` for deployments that don't want the surface.

**Participant IDs.** `GET /rooms/{roomId}/participants` reads `room_members` through the read
pool and returns a sorted JSON array of user IDs. It uses the same JWT, role cross-check,
room-membership check, and `read` rate limit as other member-facing room reads. It is a
membership list, not a live presence snapshot. Usernames and avatars remain in the host
application's user directory.

## Encryption at rest & data lifecycle

Application-level encryption is **on by default**. The gateway encrypts message content with
**AES-256-GCM** before publishing to JetStream — encrypt-before-JetStream-publish — so the
ciphertext is what both the NATS file store and Postgres persist; neither store ever holds
plaintext content at rest. Decryption happens only at delivery: at the fanout edge for live
messages, and on history reads for stored ones.

Ciphertext envelopes are **key-id-tagged**: rotation means introducing a new key id for new
messages while old keys are retained to decrypt old messages — there is no mass re-encryption
step, ever, on rotation.

**Envelope v1.** New messages use `0x01 ‖ salt(32) ‖ ciphertext ‖ tag(16)`. A per-message
AES-256 key and nonce are derived with HKDF-SHA256 from the configured key and the random salt,
so the random-nonce usage limit of AES-GCM never applies to the configured key, and the
associated data binds the ciphertext to `room_id`, `key_id` and `sender_id`: a row copied into
another room or attributed to another sender fails authentication instead of decrypting.
Envelopes written before v1 (`nonce(12) ‖ ciphertext ‖ tag(16)`, no associated data) still
decrypt while `Dotwire:Encryption:AllowLegacyEnvelopes` is `true` (the default); set it to
`false` once every legacy row has aged out of both stores to make relocation impossible.

**Key source:** keys come from configuration (`Dotwire:Encryption:Keys:<id>`, base64, exactly
32 bytes), supplied through an environment variable or an external secret store.
`Dotwire:Encryption:ActiveKeyId` must name one of them. **There is no auto-generated fallback
and no off-switch:** dotwire refuses to start without a valid active key, so a deployment can
never silently write plaintext or depend on a key that only ever lived on the data volume.

**Threat model, stated plainly:** this protects against stolen disks, stolen backups, and
direct SQL/`pg_dump` access. It is explicitly **not** a defense against a malicious server —
the server necessarily reads message content in order to fan it out to other clients. True
end-to-end encryption is out of scope for the same reason it's out of scope in PRODUCT.md: it
is a different problem than the one dotwire's threat model addresses, left to the open-source
community.

These are dotwire's own symmetric at-rest keys, entirely distinct from the host's JWT signing
keys: dotwire's **AES-256-GCM** at-rest encryption keys have nothing to do with the host's
**RS256** JWT signing keys covered in AUTH.md — the two key systems must never be conflated,
and AUTH.md presents them side by side precisely to prevent that confusion.

**Performance note:** AES-GCM runs at gigabytes-per-second on modern hardware; encryption does
not move the scale numbers stated in PRODUCT.md's scale contract.

**Data lifecycle.** Retention is configurable independently in each store, because they serve
different purposes: Timescale chunk drops govern Postgres history retention (default:
indefinite, host-configured — Postgres is the durable archive), while JetStream's `MaxAge`
governs how long the stream itself retains messages (default: roughly 48 hours — it only needs
to cover the batch-writer's lag plus the reconnect gap-fill window, not serve as long-term
storage). Retention policy applies to **metadata too, not just content** — who messaged whom
and when live in plaintext columns (ids, timestamps, room references), and a retention policy
that only touched encrypted content while leaving that metadata untouched would not actually be
a retention policy. State this plainly rather than letting it be assumed.

**Redaction mechanics.** The redact/delete message API (an admin-only endpoint) is the GDPR
Art. 17 erasure mechanism, built as a first-class feature rather than a workaround: it removes
the message's content from Postgres and deletes the message from JetStream by stream sequence
(per-message deletion with secure erase), so erasure is physical, not a soft-delete flag.
Redaction emits an audit event carrying ids only — never content. That contract, combined with
the audit log's ids-only invariant (see "Audit log" above), is what makes erasure and the audit
chain compatible: redacting a message never requires rewriting an audit entry, so the hash
chain survives every redaction intact.

**Retention mechanics.** `set_message_retention(p_days)` / `get_message_retention()` are
narrow `SECURITY DEFINER` Postgres functions (search_path pinned, same hardening pattern as
`redact_message` — see "Redaction mechanics" — so a `SECURITY DEFINER` function can't be
hijacked via a temp schema), granted to `dotwire_app` and nothing else, so the runtime role can
manage retention without holding broad DDL/administrative privileges on the hypertable itself.
Applying a retention window drops entire Timescale chunks (`drop_chunks`, chunk-level, not
row-level `DELETE`) once every row in a chunk is older than the window — this is why retention
is O(1) per chunk rather than an O(n) scan-and-delete, and why it produces zero vacuum bloat
(there are no per-row tombstones to vacuum). Manage retention from exactly one place at a
time — either `Dotwire:Retention:MessagesDays` at startup or the admin API/SDK retention
methods, not both — since the config path only takes effect at startup and an unset config
value means "leave whatever the API last set" rather than "clear it," so mixing the two is how
an operator loses track of which one is authoritative.

**DSAR export mechanics.** The DSAR export endpoint (also admin-only) is the GDPR Art. 15
access mechanism: given a `sub`, it exports that user's messages, room memberships, and role
and audit-reference metadata across every room they touch. Message content is decrypted at
export time — the same delivery-edge decryption used for history reads (see "Read path"
above) — so the export is human-readable rather than a dump of ciphertext. By design the export
is not gated by `room_members`: it is an admin compliance operation over everything the
subject sent, and it is audited (`dsar.exported`) before the first byte is written. Admin and
audit responses carry `Cache-Control: no-store`.
