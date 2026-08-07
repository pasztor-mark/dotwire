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
  responsibility matrix" below).
- **Hash-chained, append-only audit log, with logged audit access.** A single durable
  consumer computes `hash = SHA-256(prev_hash ‖ canonical_event)` over every audit event and
  batch-inserts the results into an append-only Postgres table; UPDATE/DELETE are revoked at
  the database level and backed by a guard trigger. Reading the audit log itself emits an
  audit event, so audit access is self-documenting. See ARCHITECTURE.md, "Audit log."
- **Role cross-check + membership authorization pipeline.** Every authenticated request
  verifies the JWT signature and standard claims, then cross-checks the token's `dw:role`
  claim against the service-side `user_roles` table — the table is authoritative, not the
  claim — before checking room membership against `room_members` at connect time. See AUTH.md,
  "Authorization pipeline."
- **Redact/delete message API** (admin role). Physically removes a message's content from
  Postgres and deletes it from JetStream by stream sequence (secure erase), rather than a
  soft-delete flag. Redaction emits an audit event carrying ids only, so the hash chain
  survives every redaction intact. See ARCHITECTURE.md, "Encryption at rest & data lifecycle"
  ("Redaction mechanics").
- **DSAR export endpoint** (admin role). For a given `sub`, exports that user's messages
  (decrypted), memberships, roles, and references to their audit entries. See ARCHITECTURE.md,
  "Encryption at rest & data lifecycle" ("DSAR export mechanics").
- **Configurable retention (content and metadata).** Postgres history retention (Timescale
  chunk drops) is host-configured and defaults to indefinite; JetStream stream retention
  (`MaxAge`) defaults to roughly 48 hours, since it only needs to cover batch-writer lag and
  reconnect gap-fill, not serve as long-term storage. Retention policy applies to metadata
  too — who messaged whom and when live in plaintext columns, and a retention policy that only
  touched encrypted content would not actually be a retention policy. See ARCHITECTURE.md,
  "Encryption at rest & data lifecycle."
- **Key rotation (both key systems).** dotwire's AES-256-GCM at-rest encryption keys rotate by
  introducing a new key id for new messages while old keys are retained to decrypt old
  messages — no mass re-encryption. The host's RS256 JWT signing keys rotate independently, via
  a new `kid` published at a JWKS endpoint or added to inline config. See AUTH.md, "The two key
  systems (do not confuse them)" and "Key exchange & rotation."

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
| CC7.2 / CC7.3 — system monitoring and incident detection | Hash-chained audit log, including self-logging of audit access (ARCHITECTURE.md) | Monitor the audit log operationally; define and run an incident response process |
| CC8.1 — change management | *(no dotwire mechanism — see Host obligation)* | Host owns change management for its deployment: release approval, testing, and rollout process for dotwire and its configuration |

### ISO 27001 Annex A (2022)

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| A.5.15 — access control | Role cross-check pipeline (`dw:role` vs. `user_roles`) + room membership check against `room_members` (AUTH.md) | Define the organizational access-control policy; grant/revoke roles and memberships correctly |
| A.8.10 — information deletion | Redact/delete message API + configurable retention (content and metadata) (ARCHITECTURE.md) | Choose and enforce a retention policy; invoke redaction/DSAR endpoints as part of an operational process |
| A.8.12 — data leakage prevention | AES-256-GCM at-rest encryption; audit events never contain message content, only ids | Prevent leakage in host-side systems (logs, backups, exports) outside dotwire's boundary |
| A.8.15 — logging | Hash-chained, append-only audit log (ARCHITECTURE.md) | Ship/retain logs per organizational policy; review the audit log periodically |
| A.8.24 — use of cryptography | Both key systems: AES-256-GCM at-rest encryption keys and RS256 JWT signing keys (AUTH.md, "The two key systems") | Custody and rotate the encryption key and JWT private key; document key management procedures |

### HIPAA technical safeguards (§164.312)

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| §164.312(a) — access control | Role cross-check + membership authorization pipeline (AUTH.md) | Assign unique user identities on the host side; manage emergency access procedures |
| §164.312(b) — audit controls | Hash-chained audit log (ARCHITECTURE.md) | Review audit log output as part of an operational compliance program |
| §164.312(c)(1) — integrity | Hash chain (`hash = SHA-256(prev_hash ‖ canonical_event)`) with daily checkpoint anchors, enabling tamper detection via chain verification | Act on integrity-verification failures; retain checkpoint verification records |
| §164.312(e) — transmission security | TLS in transit + AES-256-GCM at-rest encryption for content that traverses JetStream | Terminate and configure TLS correctly for the deployment's network topology |
| *(not addressed by dotwire)* | — | Business Associate Agreement (BAA), and all administrative and physical safeguards, are entirely host obligations — dotwire is software, not a covered entity or business associate |

### GDPR

| Framework requirement | dotwire mechanism | Host obligation |
|---|---|---|
| Art. 15 — right of access | DSAR export endpoint (decrypted messages, memberships, roles, audit references) | Authenticate and fulfill the data subject's request through the endpoint within the legal deadline |
| Art. 17 — right to erasure | Redact/delete message API; the hash chain survives because audit events carry ids only, never content | Decide and execute an erasure process (including any host-side copies dotwire has no visibility into) |
| Art. 25 — data protection by design and by default | AES-256-GCM at-rest encryption on by default | Keep encryption enabled; document the design choice in the host's own DPIA/records |
| Art. 30 — records of processing activities | Hash-chained audit log as an input to the host's processing-activity records | Compile and maintain the actual Art. 30 record; the audit log is a source, not the record itself |
| Art. 32 — security of processing | AES-256-GCM at-rest encryption + TLS in transit + hash-chain integrity | Perform the host's own risk assessment and select organizational measures beyond dotwire's boundary |

## Shared responsibility matrix

| dotwire provides | Host must do |
|---|---|
| AES-256-GCM at-rest encryption, on by default, both stores | Custody and back up the encryption key (external secret in production — the auto-generated fallback is dev/eval posture only) |
| TLS-capable transport | Terminate TLS and manage certificates |
| Hash-chained, append-only audit log with self-logged audit access | Custody and rotate the host's RS256 JWT private key; set the token TTL policy |
| Role cross-check + membership authorization pipeline | Grant and revoke roles/memberships correctly via the admin API; keep `user_roles` and `room_members` accurate |
| Redact/delete message API (GDPR Art. 17 mechanism) | Choose and enforce retention policy (content and metadata) for both Postgres and JetStream |
| DSAR export endpoint (GDPR Art. 15 mechanism) | Operate the org-level compliance program: policies, security training, DPAs, BAAs, DPIAs, consent management |
| Configurable retention, applied consistently to content and metadata | Encrypt and secure backups; dotwire's at-rest encryption does not automatically cover backup storage the host controls |
| Documentation mapping controls to common frameworks (this document) | Harden the surrounding infrastructure: host OS, network, container runtime, secrets store |

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
