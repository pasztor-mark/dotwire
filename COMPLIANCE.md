# dotwire — Compliance Posture

## Ready, not certified

dotwire is **compliance-ready** software. It is not, and cannot be, "SOC 2 compliant," "ISO
27001 certified," or "HIPAA compliant" on its own — certifications and attestations attach to
a **deployment** and the organization running it, not to a piece of open-source software.
Nobody audits a GitHub repository; auditors audit an operator's people, processes, and running
systems. dotwire's job is to make that audit easier by shipping the technical controls a host
needs and being honest about which controls it cannot provide.

This document exists to make that split explicit: it maps dotwire's built-in technical
controls to clauses in four common frameworks (SOC 2, ISO 27001 Annex A, HIPAA technical
safeguards, GDPR) and states plainly what remains the host's responsibility in every case.
Nothing in this document, and no other dotwire document, should be read as a claim that
dotwire itself is compliant or certified — where certification requires a control dotwire
cannot supply (a signed BAA, a security awareness training program, a documented change
management process), this document says so and hands the obligation to the host.

## Technical controls inventory

dotwire ships the following technical controls out of the box. Each is described in full in
the doc section linked.

- **At-rest encryption (AES-256-GCM, both stores).** Message content is encrypted
  before it is published to JetStream, so neither the NATS file store nor Postgres ever holds
  plaintext content at rest. See ARCHITECTURE.md, "Encryption at rest & data lifecycle."
- **TLS in transit.** Client and inter-service traffic is expected to run over TLS; TLS
  termination and certificate management are a host deployment concern (see "Shared
  responsibility matrix" below). The bundled compose files and HAProxy config are plain-HTTP,
  loopback-bound development topologies, not a TLS deployment. JWKS URLs and webhook URLs must
  be `https` unless explicitly overridden.
- **Hash-chained, append-only audit log, with logged audit access and a verify endpoint.** A
  single durable consumer computes `hash = SHA-256(prev_hash ‖ canonical_event)` over every
  audit event and batch-inserts the results into an append-only Postgres table; UPDATE/DELETE
  are revoked at the database level and backed by a guard trigger. Daily checkpoints anchor
  the chain, and `GET /audit/verify` (auditor role) walks the chain from the most recent
  checkpoint at or before the requested range — rather than from genesis every time — and
  reports whether it verifies intact. Reading the audit log itself emits an audit event, so
  audit access is self-documenting. **Every admin mutation now emits an audit event** — role
  changes, membership grants/revocations, redaction, message injection, and retention changes
  all leave an ids-only trail, not just the message-lifecycle events the log originally
  covered. `audit_log.prev_hash` is unique and checkpoints are validated on insert and again
  by `/audit/verify`. The JetStream AUDIT stream only queues events until they are chained into
  Postgres: written events are removed, and a full queue (`Nats:AuditMaxBytes`) fails audited
  calls closed instead of discarding events. See ARCHITECTURE.md, "Audit log."
- **Role cross-check + membership authorization pipeline.** Every authenticated request
  verifies the JWT signature and standard claims, then cross-checks the token's `dw:role`
  claim against the service-side `user_roles` table — the table is authoritative, not the
  claim — before checking room membership against `room_members` at connect time. See AUTH.md,
  "Authorization pipeline."
- **Redact/delete message API** (admin role). Physically removes a message's content from
  Postgres and deletes it from JetStream by stream sequence (secure erase), rather than a
  soft-delete flag. Redaction emits an audit event carrying ids only, so the hash chain
  survives every redaction intact. Redaction is checked: it answers `409` when a legal hold covers
  the message's room or sender, when the message is inside `Dotwire:Compliance:MinimumKeepDays`,
  or when `Dotwire:Compliance:AllowChanges` is `false`, and a refused redaction writes no audit
  event. An admin-only emergency-delete path (`reason` = `illegal_content` or `court_order`) is the
  only bypass; its audit event carries the reason code only. When the erasure-notice webhook is
  configured, each redaction publishes a notice once the stores are cleaned. See ARCHITECTURE.md, "Encryption at rest & data lifecycle"
  ("Redaction mechanics").
- **DSAR export endpoint** (admin role). For a given `sub`, exports that user's messages
  (decrypted), memberships, roles, and references to their audit entries. See ARCHITECTURE.md,
  "Encryption at rest & data lifecycle" ("DSAR export mechanics").
- **Configurable retention (content and metadata), API and startup config.** Postgres history
  retention (Timescale chunk drops, `O(1)` per chunk) is host-configured and defaults to
  indefinite, settable either at startup (`Dotwire:Retention:MessagesDays`) or live via the
  admin API/SDK retention methods (`GET`/`PUT /admin/retention`) — manage it from one of the
  two, not both, per ARCHITECTURE.md's "Retention mechanics." Every retention change emits a
  `retention.set` audit event. While any legal hold is active, the global retention job is
  paused (`GET /admin/retention` reports `paused`), and a non-zero retention below
  `MinimumKeepDays` is refused with `409 minimum_keep`. JetStream stream retention (`MaxAge`) defaults to roughly 48
  hours, since it only needs to cover batch-writer lag and reconnect gap-fill, not serve as
  long-term storage. Retention policy applies to metadata too — who messaged whom and when
  live in plaintext columns, and a retention policy that only touched encrypted content would
  not actually be a retention policy. Retention and redaction also apply to compressed history
  (opt-in `Dotwire:Compression:AfterDays`; see "Accepted limitations"). See ARCHITECTURE.md,
  "Encryption at rest & data lifecycle."
- **Account erasure (anonymize or delete).** `POST /admin/users/{userId}/erase` revokes the user's
  memberships and role at once, exactly as role deletion does. A background worker then either
  re-attributes the user's messages to the reserved sender id `dotwire:erased` (`anonymize`, the
  default) or deletes them (`delete`). The mode can be set per request and is final for that
  erasure. Messages that a legal hold or keep period protects are left in place, and the erasure
  finishes by itself once they become eligible. JetStream copies inside `Nats:RoomsMaxAgeHours` are
  deleted too. The request and its completion each emit an ids-only audit event, and
  `GET /admin/erasures/{id}` reports status computed from the stores. Anonymization is not the same
  as anonymity in small rooms; see "Accepted limitations." See ARCHITECTURE.md, "Encryption at rest
  & data lifecycle."
- **Legal hold.** An admin places a hold on a room or a user (`POST /admin/holds`, with an opaque
  `matterRef`). While it is active, redaction and erasure of held data answer `409 legal_hold`, and
  retention is paused. Only the audited emergency-delete path bypasses a hold. Placing and releasing a
  hold each emit an ids-only audit event, and `matterRef` never enters the audit log.
- **Erasure notices (durable webhook).** When `Dotwire:Webhooks:Erasure:Url` is set, every redaction
  and every erasure pass sends a signed `ErasureNotice` that lists the exact `(roomId, seq)` pairs
  affected. Notices go through a durable JetStream work queue, so delivery is at-least-once and
  receivers must deduplicate. Notices carry ids only. Each failed delivery attempt is logged and
  counted as `dotwire.webhook.erasure.failed`.
- **Compliance settings (config-only).** `Dotwire:Compliance:AllowChanges`,
  `Dotwire:Compliance:MinimumKeepDays` and `Dotwire:Compliance:Erasure:Mode` are set per deployment in
  configuration and validated at startup. No API can change them, so a stolen admin token cannot
  weaken them. `GET /admin/compliance` reads them. dotwire ships no regime presets in code; the
  suggested settings per regime live in `docs/compliance-presets.md`.
- **Key rotation (both key systems).** dotwire's AES-256-GCM at-rest encryption keys rotate by
  introducing a new key id for new messages while old keys are retained to decrypt old
  messages — no mass re-encryption. The host's RS256 JWT signing keys rotate independently, via
  a new `kid` published at a JWKS endpoint or added to inline config. See AUTH.md, "The two key
  systems (do not confuse them)" and "Key exchange & rotation."
- **Operational metrics without identifiers.** dotwire exports OpenTelemetry metrics over OTLP, on by
  default; `Dotwire__DisableMetrics=true` turns them off. Metrics carry no room, message, user or
  sender ids, no subjects, no SQL text and no content. Third-party meter tags are dropped or
  allowlisted by OpenTelemetry views. Traces are off by default (`Dotwire__EnableTracing=true` turns
  them on). When on, an allowlist processor keeps only method, route template, status, error type,
  RPC and database-operation tags, and drops spans that carry exception events. See ARCHITECTURE.md,
  "Observability," and README, "Metrics" and "Tracing."

## Control mappings

The tables below map framework requirements to the dotwire mechanism that satisfies them, and
name what stays the host's obligation. These mappings are a starting point for a host's own
auditor conversation, not a substitute for one — frameworks are satisfied by an organization's
full control environment, of which dotwire's technical controls are one input.

### SOC 2 (trust services criteria)

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| CC6.1 / CC6.6 — logical access controls | JWT signature/claims verification + role cross-check pipeline against `user_roles` and `room_members` (AUTH.md) | Manage who holds `admin`/`auditor` roles; issue and scope host-side JWTs correctly |
| CC6.7 — transmission and at-rest protection | TLS in transit + AES-256-GCM at-rest encryption (ARCHITECTURE.md) | Terminate and manage TLS; supply and custody the encryption key in production |
| CC7.2 / CC7.3 — system monitoring and incident detection | Hash-chained audit log, including self-logging of audit access (ARCHITECTURE.md); OpenTelemetry metrics for writer lag against the ROOMS max age, audit backlog against its cap, audit-writer presence and audit `503`s (README, "Metrics") | Monitor the audit log and the OTLP metrics operationally, alert on the README thresholds; define and run an incident response process |
| CC8.1 — change management | *(no dotwire mechanism — see Host obligation)* | Host owns change management for its deployment: release approval, testing, and rollout process for dotwire and its configuration |

### ISO 27001 Annex A (2022)

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| A.5.15 — access control | Role cross-check pipeline (`dw:role` vs. `user_roles`) + room membership check against `room_members` (AUTH.md) | Define the organizational access-control policy; grant/revoke roles and memberships correctly |
| A.8.10 — information deletion | Redact/delete message API, account erasure (`anonymize` or `delete`), legal holds that block erasure and pause retention, and configurable retention (content and metadata) (ARCHITECTURE.md) | Choose and enforce a retention policy and a hold procedure; record hold placement and release; invoke redaction, erasure and DSAR endpoints as part of an operational process |
| A.8.12 — data leakage prevention | AES-256-GCM at-rest encryption; audit events never contain message content, only ids | Prevent leakage in host-side systems (logs, backups, exports) outside dotwire's boundary |
| A.8.15 — logging | Hash-chained, append-only audit log of admin, auditor and compliance actions (ARCHITECTURE.md). Member reads and live deliveries are not logged | Ship/retain logs per organizational policy; review the audit log periodically |
| A.8.24 — use of cryptography | Both key systems: AES-256-GCM at-rest encryption keys and RS256 JWT signing keys (AUTH.md, "The two key systems") | Custody and rotate the encryption key and JWT private key; document key management procedures |

**Operator note (CC6.1, CC6.6 and A.5.15):** the mappings above describe the default configuration,
with the REST auth cache off. Enabling `Dotwire:AuthCacheSeconds` widens the REST revocation window to
that many seconds (at most 60) per node, so that choice is the operator's to make and record in access-control
policy. Keep the value as low as load allows.

### HIPAA technical safeguards (§164.312)

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| §164.312(a) — access control | Role cross-check + membership authorization pipeline (AUTH.md) | Assign unique user identities on the host side; manage emergency access procedures |
| §164.312(b) — audit controls | **Partial.** The hash-chained audit log (ARCHITECTURE.md) records admin, auditor and compliance actions. It does not record member activity: which user opened a room, read its history or received its messages is not recorded anywhere (see "Accepted limitations") | Review audit log output as part of an operational compliance program; record access to ePHI in chat by other means, or treat chat access as unlogged in the risk analysis |
| §164.312(c)(1) — integrity | Hash chain (`hash = SHA-256(prev_hash ‖ canonical_event)`) with daily checkpoint anchors and a `GET /audit/verify` endpoint, enabling tamper detection via chain verification | Act on integrity-verification failures; archive `audit_checkpoints` rows to independent cold storage on a regular cadence so a verification anchor survives even if the live database is compromised, and retain checkpoint verification records |
| §164.312(e) — transmission security | TLS in transit + AES-256-GCM at-rest encryption for content that traverses JetStream | Terminate and configure TLS correctly for the deployment's network topology |
| *(not addressed by dotwire)* | — | Business Associate Agreement (BAA), and all administrative and physical safeguards, are entirely host obligations — dotwire is software, not a covered entity or business associate |

### GDPR

The article mappings below are our reading of each clause. Confirm them with counsel.

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| Art. 15 — right of access | DSAR export endpoint (decrypted messages, memberships, roles, audit references) | Authenticate and fulfill the data subject's request through the endpoint within the legal deadline |
| Art. 17 — right to erasure | Redaction and account erasure (`anonymize` or `delete`); legal holds and keep periods are respected, and an erasure that is blocked finishes automatically when they end; JetStream copies are deleted; the hash chain survives because audit events carry ids only, never content | Delete the identity in the host's user table; review identifying free text, which anonymization does not remove; choose `delete` for DMs and small rooms; decide and execute erasure for host-side copies dotwire has no visibility into |
| Art. 17(2) and Art. 19 — notification of recipients | Erasure-notice webhook: a signed `ErasureNotice` per redaction and per erasure pass, listing the affected `(roomId, seq)` pairs, through a durable queue | Forward the notice to every recipient the host gave the data to; deduplicate, since delivery is at-least-once; apply the listed changes without re-deriving hold or keep rules |
| Art. 17(3) — exceptions to erasure | Legal hold blocks erasure of held data (our reading: Art. 17(3)(e), legal claims). The audit log keeps the erased user's `sub` (our reading: Art. 17(3)(b) and (e), records kept for a legal obligation or legal claims) | Record which exceptions the host relies on and for which data; use an opaque `sub`, so the retained audit id identifies no one once the host deletes its identity row; use the emergency path only with a reason code and a documented basis |
| Art. 18, Art. 21 and Art. 22 — restriction, objection, automated decisions | *(no dotwire mechanism)* — dotwire makes no decisions about people and does no profiling | Operate restriction, objection and automated-decision processes in the host application; see "Host responsibilities" |
| Art. 20 — data portability | DSAR export as JSON (decrypted messages, memberships, roles, audit references) | Deliver the export to the data subject through the host's own channel |
| Art. 25 — data protection by design and by default | AES-256-GCM at-rest encryption on by default | Keep encryption enabled; document the design choice in the host's own DPIA/records |
| Art. 30 — records of processing activities | Hash-chained audit log as an input to the host's processing-activity records | Compile and maintain the actual Art. 30 record; the audit log is a source, not the record itself |
| Art. 32 — security of processing | AES-256-GCM at-rest encryption + TLS in transit + hash-chain integrity | Perform the host's own risk assessment and select organizational measures beyond dotwire's boundary |

### CCPA

Our reading of the California Consumer Privacy Act, as amended. Confirm it with counsel.

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| §1798.100 and §1798.110 — right to know | DSAR export endpoint | Verify the requester and respond through the host's process |
| §1798.105 — right to delete | Account erasure (`anonymize` or `delete`). The exceptions are legal holds and the audit trail: the audit ids are kept (our reading: §1798.105(d)) | Delete the identity in the host's user table; decide which §1798.105(d) exceptions apply and record them; review identifying free text |
| §1798.130 — response procedures and deadlines | *(no dotwire mechanism)* | Track request intake, identity verification and response deadlines in the host's process |

### PIPL

Our reading of the Personal Information Protection Law of the People's Republic of China. Confirm it with counsel.

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| Art. 45 — right to access and copy | DSAR export endpoint (JSON) | Verify the requester and respond through the host's process |
| Art. 47 — deletion | Account erasure. The retention-period carve-out is covered by keep periods and legal holds: data inside them is erased when they end (our reading) | Delete the identity in the host's user table; define retention periods and holds; respond within the host's deadlines |
| Art. 44, Art. 46 and Art. 48 — choice, correction, explanation of rules | *(no dotwire mechanism)* | Host obligations; dotwire does not track consent (see "What dotwire deliberately does not do") |

## Shared responsibility matrix

| dotwire provides | Host must do |
|---|---|
| AES-256-GCM at-rest encryption, on by default, both stores | Custody and back up the encryption key (external secret in production; dotwire won't start without one) |
| TLS-capable transport | Terminate TLS and manage certificates |
| Hash-chained, append-only audit log with self-logged audit access | Custody and rotate the host's RS256 JWT private key; set the token TTL policy |
| Role cross-check + membership authorization pipeline | Grant and revoke roles/memberships correctly via the admin API; keep `user_roles` and `room_members` accurate |
| Redact/delete message API (GDPR Art. 17 mechanism) | Choose and enforce retention policy (content and metadata) for both Postgres and JetStream |
| DSAR export endpoint (GDPR Art. 15 mechanism) | Operate the org-level compliance program: policies, security training, DPAs, BAAs, DPIAs, consent management |
| Configurable retention, applied consistently to content and metadata | Encrypt and secure backups; dotwire's at-rest encryption does not automatically cover backup storage the host controls |
| Documentation mapping controls to common frameworks (this document) | Harden the surrounding infrastructure: host OS, network, container runtime, secrets store |
| Legal holds (`POST`/`DELETE /admin/holds`) and the emergency-delete path | Decide when to place and release a hold; keep the matter record outside dotwire (only an opaque `matterRef` is stored); document the basis for each emergency deletion |
| Account erasure (`POST /admin/users/{id}/erase`) | Delete the identity in the host's user table; review identifying content; choose `delete` for DMs and small rooms; keep `sub` values unique to one person |
| Erasure notices (`ErasureNotice` webhook) | Verify and deduplicate notices; forward them to the host's own recipients; alert on `dotwire.webhook.erasure.failed` |
| Compliance settings (`Dotwire:Compliance:*`) | Set `AllowChanges`, `MinimumKeepDays` and `Erasure:Mode` per deployment in configuration, using the suggested settings in `docs/compliance-presets.md` as a starting point |

## Accepted limitations (read before mapping a control)

- **Erasure and retention functions are callable by the runtime role.** `redact_message` (the
  unchecked form, used by the emergency path), `redact_message_checked`, `reattribute_message`,
  `set_message_retention`, `get_message_retention` and `sync_retention_schedule` are `SECURITY
  DEFINER` functions granted to `dotwire_app` and emit no audit event themselves; the API audits
  before calling them. The hold and erasure tables also accept `INSERT` from `dotwire_app`. Anyone
  holding `dotwire_app` credentials can call them directly without an audit trail, so treat
  those credentials as privileged and monitor database access.
- **The audit chain is unkeyed.** `hash = SHA-256(prev_hash ‖ canonical_event)` detects edits
  only relative to an anchor the attacker cannot rewrite. Export `audit_checkpoints` rows to
  WORM or otherwise independent storage on a schedule, and alert on DDL against the audit
  tables; the database-side checkpoint trigger and `/audit/verify` anchor check stop forged
  checkpoints written through the app role, not an attacker with owner access.
- **Audit events are published before the database commit.** A failed commit can leave an
  audit event for a change that did not happen; retrying the (idempotent) admin call then
  produces the change and a second event. The chain never misses a change that did happen.
- **DSAR export bypasses room membership by design** (see AUTH.md) and is audited as
  `dsar.exported`. Admin reads of role and membership metadata are not individually audited.
- **Revocation of live sockets is eventually consistent.** A lost NATS broadcast is caught by
  the gateway membership sweep, so the window is `Dotwire:MembershipSweepSeconds` (default 60),
  plus read-replica lag where the read pool is a replica.
- **REST revocation is eventually consistent when the auth cache is enabled.** With
  `Dotwire:AuthCacheSeconds` above `0` (opt-in, default `0`, maximum `60`), a revoked user can keep
  REST room access on a node for up to `Dotwire:AuthCacheSeconds`, plus read-replica lag where the
  read pool is a replica. With the default `0`, REST checks the database on every request.
- **Applied migrations are checksummed.** Startup fails if an already-applied migration file
  changed, so schema drift from edited history is visible rather than silent.
- **Compressed history is a storage measure, not a control.** With `Dotwire:Compression:AfterDays`
  set (opt-in, off by default), older `messages` chunks are compressed. Compression changes the
  storage layout only: it adds no encryption or access control, and it does not change how
  content is protected (content is already encrypted before it is written). Redaction still
  physically removes rows from compressed chunks, and the redaction tombstone still blocks
  re-inserts. Retention (`drop_chunks`) still removes whole compressed chunks. On TimescaleDB 2.32
  a bloom filter skips most batches for the sender scans that DSAR export and erasure use; older
  versions may read every batch, which makes those scans slower over compressed history.
  An erasure update on a compressed chunk decompresses one batch per row; the updated rows stay
  uncompressed until the compression policy recompresses the chunk. Held data stays compressed.
  Compression is a TimescaleDB Timescale License feature, included in the `timescale/timescaledb`
  image. Our reading of the TSL is that it allows free self-hosting but not offering TimescaleDB
  itself as a hosted service. Confirm against TimescaleDB's license terms before any hosted
  offering. Apache-only builds stay uncompressed.
- **Third-party processors behind your webhooks are your subprocessors.** dotwire sends presend,
  postsend and erasure-notice payloads to URLs the host configures. Whatever a host runs behind
  those URLs (moderation services, archives, search indexes) processes that data under the host's
  own arrangements, so the host covers it in its DPA, records of processing and DPIA.
- **Audit ids are kept after erasure.** The `sub` appears in the audit events `role.set.*`,
  `role.deleted`, `member.added`, `member.removed`, `dsar.exported`, `message.injected`,
  `user.erasure.requested` and `user.erased`. The table is append-only and hash-chained, so these
  ids cannot be removed. Our reading is that the audit trail may be kept under GDPR Art. 17(3)(b)
  and (e), CCPA §1798.105(d) and the PIPL retention-period carve-out; confirm this with counsel. Use
  an opaque `sub` (see "Host responsibilities"), so the retained id identifies no one once the host
  deletes its identity row.
- **Anonymization is weak in small rooms.** Anonymized messages keep their room and timestamp, and
  free text can identify a person. In a DM or a three-person room, the remaining messages can still
  point to the person; our reading is that this is not erasure under GDPR Recital 26. dotwire cannot
  tell a DM from a group room, so the host uses `delete` for those rooms or redacts their messages first.
- **Erasure completeness depends on the revocation window.** Access is revoked at once, but a live
  socket can keep sending until the window passes. The window is `Dotwire:MembershipSweepSeconds`,
  or `Dotwire:AuthCacheSeconds` if that is larger, with a 5-second minimum. If the sweep is `0`, the
  token lifetime (`Auth:MaxTokenLifetimeMinutes`) is used instead. If both are `0`, the worker uses
  60 minutes and startup logs a warning. Completion waits for the window to pass.
- **A hold cannot stop a retention run already in progress.** Pausing the retention job does not
  stop a run that has started, so chunks it has already dropped stay dropped. `POST /admin/holds`
  waits up to `Dotwire:Compliance:HoldRetentionWaitSeconds` (default 30) for a running job to finish.
  It answers `201` either way, and logs a warning if the wait times out.
- **JetStream retention is not paused by a hold.** The `ROOMS` stream keeps its `MaxAge`
  (`Nats:RoomsMaxAgeHours`, default 48). Postgres is the record, so JetStream copies of held messages
  expire on schedule.
- **Erasure notices expire.** Notices wait in the `ERASURES` stream for
  `Nats:ErasureNoticesMaxAgeHours` (default 168). A receiver that is down for longer never gets them,
  and JetStream drops them without telling anyone. Alert on `dotwire.webhook.erasure.failed` and on
  receiver downtime.
- **Member activity is not audited.** The audit log records admin, auditor and compliance actions.
  It does not record a member subscribing to a room, reading its history, receiving live messages or
  reading a stream. Sends are stored with their sender and time in the messages table, but they are not
  audit events. dotwire therefore cannot answer "who read this conversation, and when". Regimes that
  expect access logging for sensitive data, such as HIPAA's audit controls (§164.312(b)), are only
  partly covered.
- **The postsend webhook is best-effort, not an archive feed.** Its queue lives in memory: a full queue
  drops the newest message, a delivery that fails `MaxAttempts` times (default 3) is dropped, and a
  restart loses everything still queued. Each drop is logged and counted as
  `dotwire.webhook.postsend.dropped`. Admin-injected messages skip it unless `IncludeAdminSends` is set.
  A host that must archive every message, as MiFID II and SEC 17a-4 require, cannot rely on postsend
  alone. `seq` rises within a room but is not contiguous (it is the stream sequence), so a missing
  message cannot be spotted from a gap. The archive must instead page each room's history after the
  last archived `seq` on a schedule, as a room member, and treat postsend only as the fast path.
- **Admin actions have no built-in second approver.** Any admin can run any admin action, including
  emergency deletion with a reason code. Two-person control is built by the host, with audit review as
  the check (see "Approving admin actions" under "Host responsibilities", and AUTH.md).
- **Erasure retries in memory are lost on a worker restart.** The erasure worker keeps pending
  JetStream deletes and partial notices in memory. After a restart those retries are lost: JetStream
  copies remain until `Nats:RoomsMaxAgeHours`, and the partial notices are not re-sent. The erasure
  then completes from Postgres and the `completed` notice follows.
- **Erasure notice delivery is at-least-once.** A notice is re-sent after any failed or unacknowledged
  delivery, so receivers must deduplicate, for example on the event type, `roomId`, `seq` and
  `erasureId`.
- **A hold placed during a retry can block the retry.** If a redaction or emergency call answers `503`
  after the Postgres row is removed (a failed JetStream delete, or `503 notice_unavailable`), the retry
  runs against held data and answers `409 legal_hold` once a hold exists. The removed-data notice is
  then never re-sent, and the JetStream copy stays until it expires. The host's own process must
  notify its recipients in that case.
- **Erasure completion can lag.** Before it scans or declares completion, the erasure worker waits for
  the `postgres-writer` consumer to drain to the stream's last sequence, and for async read replicas to
  replay past the primary's WAL position. Under load, or with a lagging replica, an erasure reports
  `in_progress` or `waiting` for longer.

## Host responsibilities

dotwire does not do the following. They are the host's, in its own application, and the
mappings above assume the host handles them:

- **Restriction (GDPR Art. 18, CCPA, PIPL).** dotwire has no restriction flag. A host restricts
  processing for a person by removing that person's room memberships, which stops live access and
  is audited as `member.removed`. The host keeps its own record of the restriction and of when it
  applies.
- **Objection (GDPR Art. 21).** dotwire has no objection workflow. The host decides whether an
  objection stops processing and applies the outcome through memberships, redaction or erasure.
- **Automated decisions (GDPR Art. 22).** dotwire makes no decisions about people and does no
  profiling. Any automated decision the host builds on chat content (moderation through the presend
  hook, ranking, scoring) is the host's, including its explanation and human-review duties.
- **Consent.** dotwire does not record or check consent (see "What dotwire deliberately does not
  do"). The host decides the lawful basis for processing, records consent or other basis in its own
  systems, and does not need to tell dotwire about it.
- **Identity deletion.** dotwire does not own identities. An erasure does not delete the host's user
  row. The host deletes its identity (and its own copies of the person's data) when it requests an
  erasure, so that the opaque `sub` no longer links to a person.
- **Opaque `sub`.** Use a random or otherwise meaningless `sub`, not an email address, a name or
  another personal identifier. Audit events keep the `sub` after erasure (see "Accepted
  limitations"), so an opaque value is what stops them from identifying anyone. Do not reuse a `sub`
  for a different person: while an erasure is open for a `sub`, role and membership grants for it
  answer `409 erasure_open`, and reusing it after completion would attach new data to the old audit trail.
- **Approval of admin actions.** dotwire has no approval step: one `admin` token can make any admin
  call. A host that needs two-person control builds it in front of dotwire, as described in
  "Approving admin actions" below.

### Approving admin actions

dotwire runs an admin call as soon as a valid `admin` token makes it. To require one or more approvers
for admin decisions, the host puts an approval step in its own application:

1. **Keep admin tokens in the host backend.** People never receive an `admin` token. They request an
   action in the host's application, and only the backend signs admin tokens and calls dotwire.
2. **Approve, record, then call once.** Before the backend makes the call, it records the requester,
   each approver, the time, the reason and the target ids (`roomId`, `seq`, `userId`, hold id). It
   makes the call only after the required approvals.
3. **Cover every admin decision**, not only the destructive ones. Each action below writes an audit
   event that carries the same target ids:

| Admin action | Endpoint | Audit event |
|---|---|---|
| Grant or change a role | `PUT /admin/users/{userId}/role` | `role.set.<role>` |
| Remove a role | `DELETE /admin/users/{userId}/role` | `role.deleted` |
| Add room members | `POST /admin/rooms/{roomId}/members` | `member.added` (and `role.set.member` when `ensureMemberRole` creates the role) |
| Remove a room member | `DELETE /admin/rooms/{roomId}/members/{userId}` | `member.removed` |
| Inject a message | `POST /admin/rooms/{roomId}/messages` | `message.injected` |
| Redact a message | `DELETE /admin/rooms/{roomId}/messages/{seq}` | `message.redacted` |
| Emergency deletion | `POST /admin/rooms/{roomId}/messages/{seq}/emergency-delete` | `message.redacted.emergency` |
| Change retention | `PUT /admin/retention` | `retention.set` |
| Export a person's data | `GET /admin/users/{userId}/export` | `dsar.exported` |
| Place a legal hold | `POST /admin/holds` | `hold.placed` |
| Release a legal hold | `DELETE /admin/holds/{id}` | `hold.released` |
| Erase an account | `POST /admin/users/{userId}/erase` | `user.erasure.requested`, then `user.erased` when it completes |

4. **Reconcile the audit log against the approvals.** The `auditor` identity periodically matches
   every audit event in the table to an approval record. An event without one means someone called
   dotwire outside the approval step, for example with the JWT signing key, a leaked admin token, or
   the `dotwire_app` database credentials (see "Accepted limitations").

The approval step prevents mistakes and abuse by people who go through the host's application.
Reconciliation detects calls that bypass it. Neither stops someone who holds the signing key, so key
custody stays the control that everything else rests on.

Calls the host's code makes on its own, such as adding a member who joined with an invite code or
posting a system message, follow the host's application rules instead of a per-call approval. Review
those code paths like any other code, and expect their audit events in the reconciliation.

The actor in each audit event is the `sub` of the admin token that made the call. If the backend uses
one service identity for every call (the SDKs default to `host-admin`), dotwire's log cannot tell
operators apart, and the host's approval record is the only record of who decided. A host that wants
dotwire's log to name the operator who executed the action can sign admin tokens per operator
(`adminUserId` in `@dotwire/host`, `AdminUserId` in `Dotwire.Host`), each holding the `admin` role in
dotwire.

## What dotwire deliberately does not do

dotwire does not attempt to be an org-level compliance product, and says so honestly rather
than half-building the org-level layer and calling it done:

- **No policy management.** dotwire has no concept of an organizational security policy
  document; that lives wherever the host already manages policy.
- **No consent tracking.** dotwire does not record or manage user consent for data processing
  — that is a host application concern, upstream of anything dotwire touches.
- **No DPIA tooling.** Data Protection Impact Assessments are the host's responsibility to
  produce; dotwire's control mappings above are an input to one, not a substitute for one.
- **No compliance dashboards.** dotwire ships APIs (redaction, DSAR, audit access) and audit
  data; a host builds or buys whatever reporting UI it wants on top, the same way PRODUCT.md
  scopes out an admin dashboard UI in general.
- **No end-to-end encryption.** Out of scope for the same reason PRODUCT.md states it: dotwire's
  threat model is stolen disks, stolen backups, and direct database access — not a malicious
  server — and the server must read message content to fan it out. See PRODUCT.md, "Out of
  scope."
- **Metadata is stored in plaintext, on purpose.** Who messaged whom and when live in plaintext
  columns (ids, timestamps, room references) because query patterns need them queryable —
  encrypting metadata would make basic operations like listing a room's history impractical.
  The control for metadata is **retention**, not encryption: a short, enforced retention window
  limits how long that plaintext metadata exists, and that is the honest tool for this job
  rather than pretending encryption covers something it doesn't.
