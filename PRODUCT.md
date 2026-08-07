# dotwire — Product

## What this is

dotwire is a fully free, open-source, self-hostable **chat layer** — infrastructure a host
application plugs in, not a chat product in its own right. It has no user system of its own:
the host app owns identity and mints JWTs, and dotwire's only job is to verify those tokens
and authorize what the bearer can do. This split is deliberate. Chat vendors like Stream,
Sendbird, and PubNub sell you both a message bus and a second, parallel user system to keep
in sync with your real one; dotwire refuses the second half of that bargain so a host never
has to reconcile two sources of truth for who its users are.

The primary audience is a host application — typically a SaaS backend — that wants embedded
chat without a recurring vendor bill and without standing up (or federating with) its own
user database. Secondary uses exist and are supported, though intentionally not optimized
for: agent-to-agent messaging, and human-in-the-loop or harness observability, where a
message bus with humans watching and occasionally intervening is a useful shape even though
no human "chat" is happening. dotwire originated as a replacement for a Matrix Conduit
instance running inside the owner's other project — Conduit's federation machinery and
operational weight solved problems dotwire's actual users don't have.

dotwire deliberately does not federate. Federation is the single most expensive design axis
Matrix-style systems take on, and it buys interoperability with servers dotwire's target
operators don't need to talk to. Every host runs its own island; that constraint is what
keeps the rest of the system simple enough to self-host in an afternoon.

## Who it's for

The primary audience is host applications — most often a SaaS backend — that need embedded
chat but don't want to pay a vendor's per-MAU bill or maintain a second, shadow user table
just to satisfy a chat vendor's identity model. Because dotwire trusts host-minted JWTs and
owns no identity of its own, integration is a matter of pointing dotwire at a signing key,
not migrating or duplicating user records.

The secondary audience is agent harnesses that need an observable message bus with humans in
the loop — for example, routing agent-to-agent traffic through rooms a human operator can
watch, join, or interrupt. This is supported, but by explicit decision it is an afterthought:
where a design choice would trade off primary-audience quality for agent ergonomics, the
primary audience wins. (The one exception carved out for agents — a resumable SSE read path —
is a deferred roadmap item, not a v1 feature, precisely because agents are secondary.)

## Scale contract

dotwire targets everything up to the point where an operator's traffic justifies building
their own bespoke, distributed wide-column solution — and stops there on purpose. It does not
chase Discord scale, and says so without apology: chasing that scale would mean absorbing
Discord-grade operational complexity (custom wide-column stores, exotic sharding, dedicated
infra teams) that would make dotwire harder to self-host for the audience it actually serves.

Concretely: a single node sustains roughly 100,000–200,000 concurrent connections; a modest
cluster sustains roughly 1–2 million concurrent connections and 5,000–10,000 messages per
second sustained. That translates to a ceiling of roughly 5–15 million daily active users, or
20–50 million monthly active users. For context, that's in the neighborhood of Slack's
message volume, and roughly 1/50th of Discord's. If an operator outgrows this envelope, that
operator can by definition afford to build (or hire) their own infrastructure team — that is
the design line, stated plainly rather than apologized for. Every architecture decision in
ARCHITECTURE.md is made against this ceiling, not against Discord's.

## Design pillars

1. **Host-owned identity.** dotwire keeps no user table of its own; it verifies JWTs the host
   already mints and authorizes against membership and role tables it manages. The payoff is
   integration effort measured in an afternoon, not a migration project — there is no user
   data to import, sync, or reconcile because dotwire never owned any.

2. **Plug and play.** One binary, one config flag (`all` by default) runs the whole system on
   a single compose file, with no federation to configure and no second cluster to stand up
   for a small deployment. The same binary scales up later by flipping the role flag
   (`api` / `gateway`) rather than by adopting a different deployment model — small and large
   deployments are the same software, not a "starter" and a "real" edition.

3. **Compliance-grade auditability.** The audit log is append-only and hash-chained, so
   tampering is detectable rather than merely discouraged. Reading the audit log is itself
   logged, so "who looked at the audit trail" is answerable. Audit access is deliberately not
   room access — an auditor role can prove what happened without being able to read live
   conversations. And message content is encrypted at rest by default (AES-256-GCM), in both
   Postgres and the NATS stream store, so a stolen disk or a leaked `pg_dump` doesn't hand
   over plaintext. These exist because "compliance-ready" is a claim dotwire wants to be able
   to make honestly, not retrofit later.

4. **Scarce-dimension honesty.** In a chat system, ingest is cheap and fanout/concurrency are
   where systems bleed — most connections are idle at any instant, but every one of them costs
   memory and every message fans out to all of them. The architecture spends its discipline
   budget on that axis deliberately (WebSockets-only, no per-connection replay buffers,
   authorize-at-connect not authorize-per-message, and more — see ARCHITECTURE.md,
   "Concurrency and fanout disciplines" for the full set), rather than spreading effort evenly
   across problems that aren't actually expensive at this scale.

## Out of scope — a contract, not a suggestion

These are deliberate exclusions. Do not add them "helpfully."

- **End-to-end encryption** — left to the open-source community; dotwire's threat model is
  stolen disks and database leaks, not a malicious server, and the server must read content
  to fan it out (see ARCHITECTURE.md, "Encryption at rest & data lifecycle").
- **AI moderation** — this is the host's problem to solve; the presend hook (below) is the
  designed extension point for it, not a built-in moderation engine.
- **Sharding implementation** — the schema is shard-*ready* via `room_id` as the shard key on
  every hot-path query, but actual sharding is excluded until an operator is past the scale
  contract above, at which point they're building bespoke infrastructure anyway.
- **Multi-region** — out of scope at this scale; the scale contract does not require it.
- **Federation** — the single biggest complexity driver in Matrix-style systems, and not a
  problem dotwire's host-owns-identity model needs to solve.
- **gRPC** — one wire protocol (SignalR/WebSockets, plus REST for send) keeps the surface
  area small; a second RPC protocol would only add maintenance cost.
- **Admin dashboard UI** — dotwire ships APIs (admin, DSAR, redaction); a host builds or buys
  whatever UI it wants on top, rather than dotwire maintaining a UI most hosts would replace
  anyway.
- **Redis** — not part of the topology; NATS/JetStream already covers fanout, ephemeral
  pub/sub, and persistence-ack, so a second moving piece would be redundant infrastructure.

## Roadmap extension points (documented, not built)

- **Presend webhook hook.** A synchronous call to a host-configured URL before a message is
  published, with a timeout budget and a configurable fail-open/fail-closed policy on
  timeout or error. This is the designed extension point for host-side moderation — it is why
  "AI moderation" can stay out of scope above without leaving hosts stranded.
- **SSE read path.** A resumable Server-Sent Events subscribe endpoint aimed at agents and
  harnesses that want a zero-dependency consumer without a full SignalR client. Deferred
  because agents are a secondary audience (see "Who it's for"); SignalR remains the only
  subscribe path in v1.

## Related docs

- [ARCHITECTURE.md](ARCHITECTURE.md) — how dotwire works: write/read paths, SignalR + NATS
  backplane topology, node roles, the audit chain, concurrency and fanout disciplines, and
  at-rest encryption.
- [AUTH.md](AUTH.md) — the normative host-integration contract: claims, key exchange and
  rotation, role semantics, and the authorization pipeline.
- [COMPLIANCE.md](COMPLIANCE.md) — compliance posture, control mappings, and the
  shared-responsibility split between dotwire and the host's deployment.
- [AGENTS.md](AGENTS.md) — how to work on this repo, including build/run/test commands and
  project conventions.
