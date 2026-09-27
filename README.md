# dotwire

**Self-Hosted Realtime Chat Infrastructure**

[![.NET 10](https://img.shields.io/badge/.NET-10.0%20Native%20AOT-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![TimescaleDB](https://img.shields.io/badge/TimescaleDB-PostgreSQL%2017-336791?style=flat&logo=postgresql)](https://www.timescale.com/)
[![NATS JetStream](https://img.shields.io/badge/NATS-JetStream%202.10-27AAE1?style=flat&logo=natsdotio)](https://nats.io/)
[![Encryption](https://img.shields.io/badge/At--Rest-AES--256--GCM-blueviolet?style=flat)]()
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)]()

dotwire is a self-hostable messaging infrastructure layer designed to be embedded into existing applications. It provides real-time chat persistence, monotonic ordering, and an auditable event history while leaving identity management with the host application.

> **Early development — not production-ready.** dotwire is under active development. Features, APIs, and deployment guidance may change without notice. Do not use it for production workloads.

---

## Key Highlights

* **Native AOT:** Compiled ahead-of-time with .NET 10 Native AOT into a standalone binary.
* **Stateless Host-Owned Identity:** Authenticates via host-signed RS256 JWTs with authoritative database role cross-checks. dotwire owns zero user tables.
* **Decoupled Persistence Pipeline:** Client send acknowledgements (`202 Accepted`) confirm NATS JetStream persistence, independently of background TimescaleDB batch ingestion.
* **Shard-Key Discipline:** Hot-path queries include `room_id`; TimescaleDB hypertables support time-based retention through chunk drops.
* **Envelope Encryption at Rest:** Payloads are encrypted with AES-256-GCM (`nonce(12) ‖ ciphertext ‖ tag(16)`) at the gateway before publishing to JetStream or writing to disk. Key IDs travel alongside envelopes for zero-downtime key rotation.
* **Tamper-Evident Audit Chain:** Append-only compliance log hash-chained with SHA-256, anchored by periodic checkpoints, verifiable via `GET /audit/verify`, and protected with database-level revoked grants and guard triggers.
* **Compliance Surface:** Redaction, DSAR export, and configurable retention (Postgres chunk drops + JetStream `MaxAge`) via the admin API and both host SDKs.
* **Webhooks & SSE:** Optional presend/postsend webhook call-outs for host-side moderation, and a resumable Server-Sent Events read path alongside SignalR.
* **Per-Node Rate Limiting:** In-process token-bucket limits on send, read, admin, and hub routes — no external dependency.

---

## Architecture Overview

```
                      ┌────────────────────────────────────────────────────────┐
                      │              HOST CLIENT / WEB FRONTEND                │
                      └────────────────────────────────────────────────────────┘
                                                   │
                            POST /rooms/{roomId}/messages (RS256 JWT)
                                                   ▼
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ dotwire Gateway Node (.NET 10 Native AOT)                                                    │
│                                                                                              │
│  1. JwtBearer: Validate RS256 Signature + Exp + Claims                                       │
│  2. RoleCrossCheckFilter: SELECT role FROM user_roles (Read Pool)                            │
│  3. RoomMembershipFilter: SELECT EXISTS FROM room_members (Read Pool)                        │
│  4. MessageCipher: Encrypt Payload (AES-256-GCM under active key)                            │
│  5. JetStream Publish: Publish to NATS stream ROOMS (subject: room.{roomId})                  │
│  6. Client Ack: Return HTTP 202 Accepted with (seq, time)                                    │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
                                                   │
                        JetStream Ingest (TCP)     │     Background Batch Insert
                                                   ▼
             ┌──────────────────────────┐     ┌──────────────────────────┐
             │    NATS JetStream 2.10   │ ──► │  PostgresWriterService   │
             │   (File-Backed Storage)  │     │   (≤ 500 msgs / 1s batch)│
             └──────────────────────────┘     └──────────────────────────┘
                                                           │
                                                           ▼
                                              ┌──────────────────────────┐
                                              │   TimescaleDB (Postgres) │
                                              │  (messages Hypertable)   │
                                              └──────────────────────────┘
```

---

## Quickstart (Docker Compose)

### 1. Prerequisites
* [Docker](https://docs.docker.com/get-docker/) & Docker Compose
* OpenSSL (for generating test keys)

### 2. Configure Environment
```bash
# Clone the repository
git clone https://github.com/pasztor-mark/dotwire.git
cd dotwire

# Create .env from template
cp .env.example .env

# Generate a 32-byte AES-256-GCM symmetric encryption key
echo "Dotwire__Encryption__Keys__k1=$(openssl rand -base64 32)" >> .env

# Generate dev RSA private/public keypair for RS256 token minting
openssl genpkey -algorithm RSA -out dev-jwt.key -pkeyopt rsa_keygen_bits:2048
openssl rsa -pubout -in dev-jwt.key -out dev-jwt.pub

# Set test passwords in .env
echo "POSTGRES_PASSWORD=dotwire_dev_password" >> .env
echo "POSTGRES_APP_PASSWORD=dotwire_app_password" >> .env
echo "Auth__Issuer=dev-host" >> .env
echo "Auth__Audience=dotwire" >> .env
echo "Auth__Keys__dev=$(cat dev-jwt.pub)" >> .env
```

### 3. Launch Stack
```bash
docker compose up -d --build
```

### 4. Verify Health
```bash
curl http://localhost:8080/healthz
# Response: 200 OK
```

### 5. Seed Test Room & Users
```bash
docker compose exec postgres psql -U dotwire -d dotwire -c "
  INSERT INTO user_roles (user_id, role) VALUES ('user-1', 'member'), ('admin-user', 'admin') ON CONFLICT DO NOTHING;
  INSERT INTO room_members (room_id, user_id) VALUES ('00000000-0000-0000-0000-000000000001', 'user-1') ON CONFLICT DO NOTHING;
"
```

---

## Building & Testing Locally

Requires the **.NET 10 SDK** (`global.json` pins `10.0.0`):

```bash
# Build the solution
dotnet build dotwire.slnx

# Run the test suite (Compose integration tests use Postgres and NATS when available)
dotnet test dotwire.slnx

# Publish standalone Native AOT binary
dotnet publish dotwire/dotwire.csproj -c Release
```

---

---

## Official SDKs

Coding agents integrating chat into a host app can start with the [SDK guide](docs/SDK_AGENT_GUIDE.md), which maps each package to its responsibility and shows the participant lookup flow.

| Package | Environment | Purpose | Documentation |
|---|---|---|---|
| [`@dotwire/client`](packages/client) | Browser / Web / React / Vue / Node.js | Real-time chat, scoped rooms, SignalR subscription with gap-fill, typing, presence, history | [Client README](packages/client/README.md) |
| [`@dotwire/host`](packages/host) | Node.js / Next.js / Express Backend | RS256 JWT minting, zero-SQL room provisioning, role administration, system messages, webhooks, moderation, audit, retention, DSAR | [Host README](packages/host/README.md) |
| [`@dotwire/react`](packages/react) | React 18+ | `DotwireProvider`, `useRoom`, `useTyping`, `usePresence`, `useConnectionState` hooks over `@dotwire/client` | [React README](packages/react/README.md) |
| [`Dotwire.Host`](Dotwire.Host) | .NET 8/9/10 C# Backend | C# RS256 JWT minting, `Presend`/`PostSend` moderation hooks, batched async review, redaction, webhooks, audit, retention, DSAR | [.NET Host README](Dotwire.Host/README.md) |

---

## Normative Specifications & Documentation

The architecture is governed by normative contracts:

1. **[PRODUCT.md](PRODUCT.md)** — Product scope, user personas, and the hard out-of-scope contract.
2. **[ARCHITECTURE.md](ARCHITECTURE.md)** — Write path, read path, NATS backplane, node roles, and fanout disciplines.
3. **[AUTH.md](AUTH.md)** — Token specification, claims format, key rotation, and 4-stage authorization pipeline.
4. **[COMPLIANCE.md](COMPLIANCE.md)** — Compliance controls inventory, encryption, redaction, and audit logging.
5. **[AGENTS.md](AGENTS.md)** — Hard architectural conventions for AI pair programmers and human contributors.

---

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
