# dotwire

**High-Throughput, Self-Hosted Realtime Chat Infrastructure**

[![.NET 10](https://img.shields.io/badge/.NET-10.0%20Native%20AOT-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![TimescaleDB](https://img.shields.io/badge/TimescaleDB-PostgreSQL%2017-336791?style=flat&logo=postgresql)](https://www.timescale.com/)
[![NATS JetStream](https://img.shields.io/badge/NATS-JetStream%202.10-27AAE1?style=flat&logo=natsdotio)](https://nats.io/)
[![Encryption](https://img.shields.io/badge/At--Rest-AES--256--GCM-blueviolet?style=flat)]()
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)]()

dotwire is a high-performance, self-hostable messaging infrastructure layer designed to be embedded into existing applications. It delivers real-time chat persistence, monotonic ordering guarantees, and compliance audit chains while keeping resource utilization sub-50 MB RAM per node.

---

## Key Highlights

* **Native AOT Performance:** Compiled ahead-of-time with .NET 10 Native AOT into an 18 MB standalone binary with zero JIT runtime overhead.
* **Stateless Host-Owned Identity:** Authenticates via host-signed RS256 JWTs with authoritative database role cross-checks. dotwire owns zero user tables.
* **Decoupled Persistence Pipeline:** Client send acks (`202 Accepted`) confirm NATS JetStream file-backed persistence in `< 10ms`, decoupled from background TimescaleDB batch ingestion.
* **Shard-Key Discipline:** Every hot-path query is sharded by `room_id`, leveraging TimescaleDB hypertables for $O(1)$ constant-time chunk pruning (`drop_chunks`) with zero PostgreSQL vacuum bloat.
* **Envelope Encryption at Rest:** Payloads are encrypted with AES-256-GCM (`nonce(12) ‖ ciphertext ‖ tag(16)`) at the gateway before publishing to JetStream or writing to disk. Key IDs travel alongside envelopes for zero-downtime key rotation.
* **Tamper-Evident Audit Chain:** Append-only compliance log hash-chained with SHA-256 and protected with database-level revoked grants and guard triggers.

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

## Benchmark Performance

Empirical metrics measured under continuous concurrent load (single-node Docker deployment):

| Workload Preset | Ingest Throughput | Simulated Audience | Broadcast Fanout | Total Cluster RAM | Total Cluster CPU |
|---|---|---|---|---|---|
| 👥 **Small Team** | `30 msgs/s` | `500 subs` | `15,000 deliv/s` | **`178 MB`** | **`12.5% (⅛ core)`** |
| 🏢 **Enterprise Fleet** | `150 msgs/s` | `10,000 subs` | `1,500,000 deliv/s` | **`181 MB`** | **`36.8% (⅓ core)`** |
| 🏟️ **Stadium Event** | `500 msgs/s` | `50,000 subs` | `25,000,000 deliv/s` | **`170 MB`** | **`49.3% (< ½ core)`** |
| ⚡ **Max Saturation** | **`1,314 msgs/s`** | `250,000 subs` | **`328,500,000 deliv/s`** | **`~191 MB`** | **`~80% of 1 core`** |

* Tail Latency: $p_{50} = \mathbf{76\text{ ms}}$, $p_{95} = \mathbf{107\text{ ms}}$, $p_{99} = \mathbf{142\text{ ms}}$ across 100 concurrent parallel workers.
* Stream Integrity: **100% Contiguous & Monotonic** JetStream sequence allocation with zero drops.

---

## Quickstart (Docker Compose)

### 1. Prerequisites
* [Docker](https://docs.docker.com/get-docker/) & Docker Compose
* OpenSSL (for generating test keys)

### 2. Configure Environment
```bash
# Clone the repository
git clone https://github.com/your-org/dotwire.git
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

# Run unit and integration tests (43 in-memory tests, zero external dependencies needed)
dotnet test dotwire.slnx

# Publish standalone Native AOT binary
dotnet publish dotwire/dotwire.csproj -c Release
```

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
