# dotwire — Auth & Host Integration Contract

## Model

dotwire issues nothing and stores no credentials. It has no signup flow, no password table,
no session store — no user system of its own at all. The HOST application holds an RS256
private key and mints JWTs; dotwire holds only public keys (via JWKS or inline PEM) and
verifies signatures. dotwire never mints a token and never could — it is not in possession of
anything that would let it.

Because dotwire has no users, `sub` is the host's user id, an opaque string dotwire never
interprets. dotwire doesn't know or care what identity system produced
it — a UUID, an email, a database primary key — it only needs `sub` to be stable and unique
per host user, since it's the join key against dotwire's own `user_roles` and `room_members`
tables. Trust flows in one direction: the host asserts identity and a claimed role via the
JWT; dotwire verifies the signature, then re-derives authorization from its own tables rather
than taking the token's word for anything beyond "this request is from `sub`."

## Token contract (normative)

Every JWT presented to dotwire must satisfy the following. This table is the contract host
integrators build against; nothing beyond it is required, and nothing on it is optional
except where marked.

| Claim | Required | Meaning |
|---|---|---|
| `iss` | yes | Host identifier; must match dotwire's configured issuer. |
| `sub` | yes | The host's user id — an opaque string, never interpreted by dotwire beyond identity/lookup. |
| `aud` | yes | Must match dotwire's configured audience. |
| `exp` / `iat` | yes | Standard expiry/issued-at claims. Short TTLs are recommended (guidance: 5–60 minutes) — see "Threat notes" for why. |
| `dw:role` | yes | Namespaced custom claim; one of `member` \| `auditor` \| `admin`. Cross-checked against the service-side `user_roles` table (see "Authorization pipeline") — the JWT's claim is treated as a hint, not a grant. |
| `dw:name` | no | Optional display name. |

**Header:** `kid` (key id) is required in every token's header — it is how dotwire selects
which public key to verify against, whether the key came from a JWKS endpoint or inline
config.

**Signing:** RS256 only. dotwire's JwtBearer configuration does not accept HS256 or any
symmetric algorithm — see "Threat notes" for why that restriction is load-bearing rather than
incidental.

## Key exchange & rotation

dotwire's config accepts **either** of two sources for the host's public key material, and
both are natively supported by ASP.NET's `JwtBearer` handler — no custom verification code is
needed on dotwire's side for either path:

- **JWKS URL** — a standard JSON Web Key Set endpoint, polled and cached by dotwire, with the
  verification key selected per-token by its `kid` header. This is the recommended path for
  any host that already runs an OIDC-style identity provider or key-serving endpoint.
- **Inline PEM public keys** — one or more PEM-encoded public keys supplied directly in
  dotwire's configuration, keyed by `kid`. This exists for airgapped or simple deployments
  that don't want to stand up (or can't reach) a JWKS endpoint.

**Rotation, JWKS path:** publish the new key under a new `kid` at the JWKS endpoint, and keep
the old key published until every token signed with it has expired. Because dotwire polls and
caches the JWKS response, there's a propagation window equal to the poll interval — old and
new keys should overlap by at least that margin, in addition to token TTL.

**Rotation, inline path:** update the config with the new PEM entry (old entries stay present
under their `kid` until their tokens expire) and reload. There's no live polling here, so
rotation timing is entirely in the host operator's hands.

## Authorization pipeline

Every authenticated request or connection goes through this pipeline, in this exact order.
The order matters: each stage is only meaningful once the stage before it has passed.

1. **Signature verification.** The token's `kid` selects a public key (from JWKS cache or
   inline config); the signature is verified against it. Fails closed — an unrecognized `kid`
   or bad signature is rejected before anything else is inspected.
2. **Standard claims.** `exp`, `aud`, and `iss` are checked. An expired token, wrong audience,
   or wrong issuer is rejected here, before any dotwire-specific claim is consulted.
3. **Role cross-check.** The token's `dw:role` claim is compared against the service-side
   `user_roles` table for that `sub`. **The table is authoritative, not the claim** — if they
   disagree, the request is denied. This is what makes a stolen or stale token claiming
   `admin` harmless if the table says otherwise: revoking or demoting a user in `user_roles`
   takes effect immediately, without waiting for outstanding tokens to expire.
4. **Room membership check.** For room-scoped operations, membership is checked against the
   `room_members` table (host-managed via the admin API). This check happens **at CONNECT,
   not per message** — the result is cached on the connection and invalidated only by
   membership-change events, per ARCHITECTURE.md's "Concurrency and fanout disciplines." A
   per-message membership query would put the database on the fanout hot path; this contract
   is what keeps it off.

**Audit access is not room access.** The `auditor` role grants read access to the audit log
and nothing else — it does not, by itself, grant membership in any room. `admin` manages the
service (roles, room membership, redaction, DSAR exports) but participates in rooms only if
also listed in `room_members` like anyone else. The three roles are independent axes, not a
hierarchy where a higher role silently subsumes a lower one's room access.

**Audit is auditor-only.** The `GET /audit`/`GET /audit/verify` surface checks for the
`auditor` role specifically — `admin` does **not** get audit access by holding the `admin`
role alone. An admin who also needs to read the audit trail needs the `auditor` role granted
separately, same as any other user. This is a deliberate separation-of-duties boundary, not
an oversight: the role that can mutate the service (admin) is not automatically the role that
can review the tamper-evident record of what happened.

**Message injection is audited, and does not grant room read access.** The admin API lets an
admin inject a message into any room as an asserted sender (`SendSystemMessageAsync` /
`sendSystemMessage`), without needing membership in that room. The sender id on the wire is
whatever the admin asserts — dotwire does not verify that id is a real member. Every
injection emits an audit event (ids only, never content, per "Audit log" in ARCHITECTURE.md).
Injecting into a room still does not let the admin *read* that room's history or live
traffic; write-by-injection and read access remain separate grants, consistent with "Audit
access is not room access" above.

**History reads follow room membership, not role.** Fetching a room's message history is
gated the same way live delivery is — by `room_members`, checked at the point of the
request — regardless of whether the caller is `member`, `auditor`, or `admin`. There is no
role-based bypass of the membership check for history: an `auditor` or `admin` who wants to
read a room's messages needs to actually be a member of that room, same as anyone else.
`GET /rooms/{roomId}/participants` follows the same rule and returns only member user IDs.

## Roles

**`member`** is the default role for ordinary chat participants. A member's room access is
governed entirely by `room_members` — being a `member` grants no rooms by itself, it only
establishes that this `sub` is a normal (non-privileged) participant wherever it is added.

**`auditor`** grants read access to the audit log (see ARCHITECTURE.md's "Audit log" section)
and nothing else — the role itself grants zero room access. If that same `sub` is also listed
in `room_members`, room access follows membership like anyone else, the same as it would for
any other role; audit access is not room access, and the two paths are unrelated. Reading the
audit log is itself an audited action, so auditor access is self-documenting in the trail it
reads.

**`admin`** manages the service: populating and editing `user_roles`, managing `room_members`
via the admin API, and operating the compliance surface (redact/delete messages, DSAR
exports — see COMPLIANCE.md). Admin is a service-management role, not an automatic room
membership or audit-read grant; an admin who needs to read a room's messages or the audit log
needs those separately, same as any other role.

Migration 0004 seeds `host-admin` with `admin`; migration 0007 seeds `host-auditor` with
`auditor`, so a host has a working auditor identity to mint tokens for (or reuse for its own
compliance tooling) without first having to grant itself the role via the admin API.

All three roles live in the service-side `user_roles` table, **preseeded with sensible
defaults but fully configurable** — an operator can rename the boundary of what each role can
do at the policy layer, though the three role names themselves (`member`, `auditor`, `admin`)
are what dotwire's own authorization code checks against. Roles are populated by seed data at
first run and can subsequently be managed via the admin API, so a host never needs direct
database access to grant or revoke a role.

## Example: minting a token (host side)

**host-side example — this code does NOT run in dotwire.** It illustrates what a host
backend's token-minting endpoint looks like; dotwire only ever consumes the result.

C# (`System.IdentityModel.Tokens.Jwt`):

```csharp
// Runs in the HOST application, not in dotwire.
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

// privateKeyPem: the host's RS256 private key, loaded from secret storage.
var rsa = RSA.Create();
rsa.ImportFromPem(privateKeyPem);

var signingKey = new RsaSecurityKey(rsa) { KeyId = "2026-08-key-1" }; // must match the kid
                                                                       // published via JWKS
                                                                       // or inline config
var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

var now = DateTime.UtcNow;
var claims = new[]
{
    new Claim(JwtRegisteredClaimNames.Iss, "my-host-app"),
    new Claim(JwtRegisteredClaimNames.Sub, hostUserId),        // opaque to dotwire
    new Claim(JwtRegisteredClaimNames.Aud, "dotwire"),
    new Claim("dw:role", "member"),                            // member | auditor | admin
    new Claim("dw:name", displayName),                         // optional
};

var token = new JwtSecurityToken(
    claims: claims,
    notBefore: now,
    expires: now.AddMinutes(15),                                // short TTL — see Threat notes
    signingCredentials: credentials);

token.Header["kid"] = signingKey.KeyId;

var jwt = new JwtSecurityTokenHandler().WriteToken(token);
```

Node.js (`jsonwebtoken`) — for hosts that aren't .NET:

```javascript
// Runs in the HOST application, not in dotwire.
const jwt = require("jsonwebtoken");

const privateKey = process.env.DOTWIRE_SIGNING_PRIVATE_KEY; // RS256 PEM, from secret storage

const token = jwt.sign(
  {
    "dw:role": "member",       // member | auditor | admin
    "dw:name": displayName,    // optional
  },
  privateKey,
  {
    algorithm: "RS256",
    issuer: "my-host-app",
    subject: hostUserId,       // opaque to dotwire
    audience: "dotwire",
    expiresIn: "15m",          // short TTL — see Threat notes
    keyid: "2026-08-key-1",    // must match the kid published via JWKS or inline config
  }
);
```

## Threat notes

- **Why the role cross-check exists.** A JWT's claims are only as trustworthy as the moment
  they were signed — a token can be stolen, or a role legitimately granted at mint time can be
  revoked before the token expires. Cross-checking `dw:role` against `user_roles` on every
  authorization decision means a demotion or revocation in the table takes effect immediately,
  independent of how long the outstanding token has left to live.
- **Why short TTLs.** Because dotwire has no token revocation list, revoking access to a
  *specific still-valid token* is bounded entirely by its TTL — the role cross-check protects
  against role drift, but a token that hasn't expired yet is still a valid credential for
  whatever it was scoped to at mint time. Short TTLs (guidance: 5–60 minutes) keep that window
  small; hosts needing tighter control should mint short-lived tokens and refresh them, not
  rely on long-lived ones.
- **Why RS256, not HS256.** RS256 is asymmetric: the host holds the private key and dotwire
  holds only the public key, so dotwire is structurally incapable of minting a valid token —
  it can only verify. HS256 is symmetric, so verifying a token would require dotwire to hold
  the same secret used to sign it, meaning any compromise of dotwire's config (or any bug that
  leaked the secret) would let an attacker mint arbitrary tokens. RS256 keeps dotwire
  permanently non-mint-capable by construction, not by policy.

## The two key systems (do not confuse them)

dotwire involves two entirely separate key systems that must never be conflated. AUTH.md
covers the first; ARCHITECTURE.md's "Encryption at rest & data lifecycle" section covers the
second in full.

| | Host JWT signing keys | dotwire encryption keys |
|---|---|---|
| Algorithm | RS256 (asymmetric) | AES-256-GCM (symmetric) |
| Who holds the secret | The HOST app (private key) | dotwire (service key) |
| dotwire holds | Public key only (JWKS or inline PEM) | The key itself |
| Purpose | Prove who a user is | Encrypt content at rest |
| Rotation | New `kid` via JWKS or config | New key id; old keys retained to decrypt old messages |

One sentence to keep them apart for good: JWT keys authenticate people; encryption keys
protect disks. Losing the first means impersonation — someone else can mint tokens claiming to
be your users; losing the second means unreadable history — dotwire's own encrypted message
content becomes permanently unrecoverable, so back it up like the archive it is.
