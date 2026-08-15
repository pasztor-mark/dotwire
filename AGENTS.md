# Working on dotwire

## Read first

Read these in order before making changes, not all at once — the later ones only matter once
you're touching the area they cover:

1. **[PRODUCT.md](PRODUCT.md)** — what dotwire is and who it's for. Its "Out of scope" section
   is a **contract**, not a suggestion: check it before proposing any feature, however small.
2. **[ARCHITECTURE.md](ARCHITECTURE.md)** — how dotwire works: the write/read paths, SignalR +
   NATS backplane, node roles, the audit chain, and the concurrency/fanout disciplines. Read
   before touching anything structural.
3. **[AUTH.md](AUTH.md)** — the normative host-integration contract: claims, key exchange and
   rotation, roles, and the authorization pipeline order. Read before touching auth, tokens, or
   roles.
4. **[COMPLIANCE.md](COMPLIANCE.md)** — compliance posture and control mappings. Read before
   touching anything compliance-adjacent: audit, encryption, retention, redaction, or exports —
   changes there can silently break a control mapping in this doc without you noticing.

## Build & run

Verified against this repo (.NET 10 SDK, `global.json` pins `10.0.0` with `rollForward:
latestMajor`):

```
dotnet build dotwire.slnx
dotnet run --project dotwire
dotnet publish dotwire/dotwire.csproj -c Release   # Native AOT publish (PublishAot=true in the csproj)
docker compose up                                  # compose.yaml at repo root, builds dotwire/Dockerfile
```

```
dotnet test dotwire.slnx
```

Tests run without Postgres/NATS (the app factory disables startup migrations); verifying the
schema end-to-end needs `docker compose up postgres` and a real run.

## Hard conventions

These are binding, not stylistic preferences — violating them either breaks the AOT build or
silently defeats a design decision documented in ARCHITECTURE.md/AUTH.md/COMPLIANCE.md.

- **Native AOT only.** `WebApplication.CreateSlimBuilder` — never `CreateBuilder`. Every type
  that gets serialized goes through the source-generated `JsonSerializerContext`; no
  reflection-based `System.Text.Json` serialization. SignalR uses the JSON hub protocol only —
  no MessagePack, and no strongly-typed `Hub<T>` (its reflection-based dispatch can't be
  source-generated around). See ARCHITECTURE.md, "Stack."
- **Shard-key discipline.** Every hot-path query includes `room_id`. See ARCHITECTURE.md,
  "Data layout."
- **Read/write data-source split.** Reads use the read Npgsql data source, writes use the write
  data source — never cross them. See ARCHITECTURE.md, "Data layout."
- **Presence, typing, and read receipts ride NATS core only** and are never persisted, never
  put on an ack path. See ARCHITECTURE.md, "Write path" (ephemeral traffic rule) and
  "Concurrency and fanout disciplines."
- **Roles/claims spelling is normative:** `dw:role`, `dw:name`, roles `member` | `auditor` |
  `admin`. See AUTH.md, "Token contract (normative)."
- **The audit table is append-only.** Never write `UPDATE`/`DELETE` against it — this is
  enforced at the database level (revoked grants plus a guard trigger), and code must not try
  to work around that. See ARCHITECTURE.md, "Audit log."
- **Audit events carry message/room/user ids, never message content.** See ARCHITECTURE.md,
  "Audit log," and COMPLIANCE.md, "Technical controls inventory."
- **Message content is encrypted (AES-256-GCM) before JetStream publish.** Never persist or log
  plaintext content anywhere, including debug logs and error messages. See ARCHITECTURE.md,
  "Encryption at rest & data lifecycle."

## Scope guardrails

Do not add anything on [PRODUCT.md's out-of-scope list](PRODUCT.md#out-of-scope--a-contract-not-a-suggestion) —
that list is a contract, not a backlog; read it before proposing a feature, don't assume this
summary covers it. Separately, do not introduce new infrastructure components beyond what
ARCHITECTURE.md already specifies: no Redis, no message brokers beyond NATS, no second
database.
