# @dotwire/host

For coding agents adding dotwire to an application, see the [SDK integration guide](https://github.com/pasztor-mark/dotwire/blob/main/docs/SDK_AGENT_GUIDE.md).

Full documentation: [https://dotwire.pasztormark.dev/docs](https://dotwire.pasztormark.dev/docs)

Official Node.js & TypeScript Host SDK for **dotwire**, providing JWT token minting, room provisioning, user role management, and system messaging without direct database access.

[![npm version](https://img.shields.io/npm/v/@dotwire/host.svg)](https://www.npmjs.com/package/@dotwire/host)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://github.com/pasztor-mark/dotwire)

## Features

- **RS256 JWT Token Minting:** Generates standard Dotwire access tokens signed with your application's private key.
- **Zero Raw SQL:** Manage user roles and room memberships via dotwire's built-in Admin API without requiring direct PostgreSQL connections (`pg`).
- **Scoped Room Handles:** High-level `host.room(roomId)` abstraction for provisioning and administration.
- **Automated System & Bot Messages:** Send verified announcements and notifications into any room.
- **Server-Side Message Ingestion:** Fetch paginated message history directly in backend services.

---

## Installation

```bash
npm install @dotwire/host
# or
pnpm add @dotwire/host
# or
yarn add @dotwire/host
```

---

## Quickstart

```typescript
import { DotwireHostClient } from '@dotwire/host';

// 1. Initialize host client with your RSA private key
const host = new DotwireHostClient({
  baseUrl: 'http://localhost:8080',
  privateKeyPem: process.env.DOTWIRE_PRIVATE_KEY_PEM!,
  keyId: 'prod-key-1',
  issuer: 'my-app-auth',
  audience: 'dotwire'
});

// 2. Mint an access token for a frontend user
const userToken = await host.mintToken('user_123', {
  role: 'member',
  displayName: 'Alice',
  ttl: '1h'
});

// 3. Manage roles and permissions
await host.setUserRole('user_123', 'member');

// 4. Provision a room and add members
const room = host.room('a0000000-0000-0000-0000-000000000001');
await room.addMembers(['user_123', 'user_456']);

// 5. Send an automated system announcement
await room.sendSystemMessage('Welcome to the room!');
```

---

## Guide & Usage Examples

### 1. Token Minting

Dotwire uses host-signed RS256 JWTs. The token contract specifies claims: `sub` (userId), `dw:role` (`member` | `auditor` | `admin`), and optional `dw:name`:

```typescript
// Standard member token
const token = await host.mintToken('alice', {
  role: 'member',
  displayName: 'Alice Smith',
  ttl: '1h'
});

// Auditor token (audit access; room history still requires room membership)
const auditorToken = await host.mintToken('auditor-dave', {
  role: 'auditor',
  ttl: '30m'
});

// Admin token (grants access to admin management API)
const adminToken = await host.mintToken('host-admin', {
  role: 'admin'
});
```

---

### 2. User Role Management

Authorize or revoke roles in Dotwire's authoritative role table:

```typescript
// Assign or update a user's role
await host.setUserRole('alice', 'member');
await host.setUserRole('dave', 'auditor');

// Retrieve a user's role
const userRole = await host.getUserRole('alice');
console.log(userRole?.role); // 'member'

// Revoke a user's role (also removes user from room memberships)
await host.deleteUserRole('alice');
```

---

### 3. Room Membership Management

Manage which users are allowed to access and subscribe to specific rooms:

```typescript
const roomId = 'a0000000-0000-0000-0000-000000000001';

// Add users to a room
await host.addRoomMembers(roomId, ['alice', 'bob', 'charlie']);

// Query authorized members for a room
const members = await host.getRoomMembers(roomId);
console.log('Room members:', members);

// Member-facing read, authorized as Alice rather than as the host admin.
const participantIds = await host.getRoomParticipants(roomId, 'alice');

// Query all rooms a user has access to
const userRooms = await host.getUserRooms('alice');
console.log('Alice rooms:', userRooms);

// Remove a user from a room
await host.removeRoomMember(roomId, 'bob');
```

---

### 4. Scoped Host Room Handle (`DotwireHostRoomHandle`)

```typescript
const room = host.room('a0000000-0000-0000-0000-000000000001');

// Membership operations
await room.addMembers(['alice', 'bob']);
const members = await room.getMembers();
await room.removeMember('bob');

// Sending system messages
await room.sendSystemMessage('Server restart scheduled in 5 minutes.');

// Sending on behalf of a room member
await room.sendAsUser('alice', 'Hello');

// Querying message history
const history = await room.getMessages({ asUserId: 'alice', limit: 50 });
```

---

### 5. Next.js API Route Integration Examples

#### Token Minting Endpoint (`app/api/auth/token/route.ts`)

```typescript
import { NextResponse } from 'next/server';
import { DotwireHostClient } from '@dotwire/host';

const host = new DotwireHostClient({
  baseUrl: process.env.DOTWIRE_BASE_URL!,
  privateKeyPem: process.env.DOTWIRE_PRIVATE_KEY_PEM!,
  keyId: 'prod-key',
  issuer: 'my-app'
});

export async function GET(req: Request) {
  // Verify session in your existing auth system (NextAuth, Supabase, Clerk, etc.)
  const sessionUser = await getSessionUser(req);
  if (!sessionUser) {
    return NextResponse.json({ error: 'Unauthorized' }, { status: 401 });
  }

  const token = await host.mintToken(sessionUser.id, {
    role: sessionUser.role ?? 'member',
    displayName: sessionUser.name,
    ttl: '1h'
  });

  return NextResponse.json({ token });
}
```

#### Room Provisioning Endpoint (`app/api/rooms/route.ts`)

```typescript
import { NextResponse } from 'next/server';
import { DotwireHostClient } from '@dotwire/host';
import { v4 as uuidv4 } from 'uuid';

const host = new DotwireHostClient({ ... });

export async function POST(req: Request) {
  const { name, memberIds } = await req.json();
  const roomId = uuidv4();

  // Authorize members
  await host.addRoomMembers(roomId, memberIds);

  // Send initial welcome message
  const room = host.room(roomId);
  await room.sendSystemMessage(`Room "${name}" created.`);

  return NextResponse.json({ roomId, name, members: memberIds });
}
```

---

## Compliance and erasure

These calls use the admin token and need the `admin` role. Holds and erasure change what dotwire
keeps; they never return message content.

```ts
const settings = await host.getComplianceSettings();

const hold = await host.placeRoomLegalHold(roomId, 'matter-2026-114'); // opaque reference only
await host.releaseLegalHold(hold.id);
const active = await host.listLegalHolds();

const started = await host.eraseUser('user-42', { mode: 'anonymize' }); // 202
const current = await host.getErasure(started.id);
// current.status: 'in_progress' | 'waiting' (current.blockedBy lists the reasons) | 'completed'

await host.emergencyDeleteMessage(roomId, seq, 'court_order');
```

## Access log

Member-scoped calls the SDK makes on a user's behalf (history, send, participants, SSE) mint their token
with `dw:actor` = `adminUserId` (default `host-admin`), so the access log attributes the read to the
host rather than to the member. `mintToken` sets `dw:actor` only when you pass `actor`; `tokenResponse` never does: tokens you hand to end-user clients stay unattributed.

`getAccessLog` reads with the auditor token. It throws `DotwireApiError` with `kind: 'Unavailable'`
(HTTP 503) only when the read cannot be recorded in the audit log, so retrying is safe. The query works
whether or not logging is enabled; a disabled log returns whatever earlier enabled periods recorded.

```ts
const page = await host.getAccessLog({ from: '2026-11-01T00:00:00Z', roomId, limit: 50 });
// page.entries: AccessLogEntry[]; page.next is an opaque cursor for the next page, or null
```

While any hold is active, retention is paused (`retentionPaused`). Erasure revokes the user's room
memberships and role at once, as `deleteUserRole` does. `anonymize` re-attributes the user's messages
to `dotwire:erased`, and `delete` removes them. Holds, minimum keep and write-once settings still apply,
so a held message keeps the erasure `waiting`. The caller cannot erase its own id, or the last admin.
While an erasure is open, role changes and member adds for that user answer `409`. Erasure does not
delete the identity from your own user table.

Erasure notices are delivered at least once, so the handler must be idempotent. Set `onErasure` and
point dotwire's `Dotwire:Webhooks:Erasure:Url` at a route that calls `handleErasureWebhook(request)`.
Notices list `{ roomId, seq }` pairs. A `partial` notice applies `mode` to exactly those messages. A
`completed` notice means dotwire holds no more messages from that user. Do not re-derive hold or keep
rules in the receiver.

```ts
const host = new DotwireHostClient({
  // ...config, including webhookSecret
  onErasure: async (notice) => {
    await myStore.applyErasure(notice);
  },
});
```

Exports write NDJSON through a sink. The sink receives each line without a trailing newline, so add one
yourself:

```ts
await host.archiveAuditLog((line) => out.write(line + '\n'), { afterId: 0 });
await host.exportRoomHistory(roomId, 'host-export', (line) => out.write(line + '\n'));
```

`archiveAuditLog` recomputes each hash from its predecessor and throws at the first break. It also
writes the checkpoints, and returns `{ count, lastId, lastHash }`, which lets the next run resume. Every
page emits an `audit.read` event. `exportRoomHistory` reads as `asUserId`, so that user must be a room
member. Add a dedicated export member first (audited as `member.added`, and visible to participants),
and remove it afterwards.

A `409` surfaces as `DotwireApiError { kind: 'Conflict' }`, with codes such as `legal_hold`,
`minimum_keep`, `changes_not_allowed`, `erasure_open` and `self_erasure`.

For room streams, `streamRoomEvents` yields `{ type: 'erased', roomId, mode, seqs }` after each erasure
page. It carries no user id. `anonymize` means the listed senders are now `dotwire:erased`, and `delete`
means the listed messages are gone.

## API Reference

### `DotwireHostClient`

| Method | Return Type | Description |
| :--- | :--- | :--- |
| `new DotwireHostClient(config)` | `DotwireHostClient` | Creates a new host client instance with RSA signing key and base URL. |
| `mintToken(userId, options?)` | `Promise<string>` | Mints an RS256 JWT access token. |
| `room(roomId)` | `DotwireHostRoom` | Returns a scoped room handle. |
| `setUserRole(userId, role)` | `Promise<UserRoleResponse>` | Sets or updates a user's role (`member`, `auditor`, `admin`). |
| `getUserRole(userId)` | `Promise<UserRoleResponse \| null>` | Gets a user's current role. |
| `deleteUserRole(userId)` | `Promise<void>` | Revokes a user's role. |
| `addRoomMembers(roomId, userIds)` | `Promise<string[]>` | Authorizes one or more users for a room. |
| `removeRoomMember(roomId, userId)` | `Promise<void>` | Removes a user from a room. |
| `getRoomMembers(roomId)` | `Promise<string[]>` | Retrieves all user IDs authorized in a room. |
| `getRoomParticipants(roomId, asUserId)` | `Promise<string[]>` | Lists participant IDs using the named user's room access. |
| `getUserRooms(userId)` | `Promise<string[]>` | Retrieves all room IDs accessible to a user. |
| `sendAsUser(roomId, userId, content, options?)` | `Promise<SendMessageResult>` | Sends as a room member; `options.idempotencyKey` becomes the `Idempotency-Key` header. |
| `sendSystemMessage(roomId, content, options?)` | `Promise<SendMessageResult>` | Sends a host announcement to a room. |
| `getMessages(roomId, options)` | `Promise<RoomHistoryResponse>` | Queries history as `options.asUserId`. |
| `handlePresendWebhook(request)` / `handlePostSendWebhook(request)` | `Promise<Response>` | Verifies and handles dotwire's webhook call-outs. Requires `webhookSecret` (or `allowUnsignedWebhooks: true`). |
| `handlePresendWebhookBody(body, signature, rawBody, { timestamp, signatureV2 })` | `Promise<WebhookResult>` | Express-style variant; pass the raw body and the `X-Dotwire-Timestamp` / `X-Dotwire-Signature-V2` headers. |
| `getComplianceSettings()` | `Promise<ComplianceSettings>` | Reads `allowChanges`, `minimumKeepDays`, `erasureMode`, `activeHolds`, `retentionPaused`, `accessLogEnabled` and `accessLogRetentionDays`. |
| `getAccessLog({ from, to?, roomId?, userId?, limit?, cursor? })` | `Promise<AccessLogPage>` | Pages the access log as the auditor. `from`/`to` accept a `Date` or ISO string; `roomId` must be a UUID. Throws `Unavailable` (503) when the read cannot be recorded in the audit log. |
| `placeRoomLegalHold(roomId, matterRef)` / `placeUserLegalHold(userId, matterRef)` | `Promise<LegalHold>` | Places a hold. `matterRef` is stored in plaintext: pass an opaque reference. |
| `releaseLegalHold(holdId)` | `Promise<void>` | Releases a hold. Releasing it twice answers `409` (`Conflict`). |
| `listLegalHolds({ activeOnly? })` | `Promise<LegalHold[]>` | Lists holds. `activeOnly` defaults to `true`. |
| `emergencyDeleteMessage(roomId, seq, reason)` | `Promise<RedactMessageResponse>` | Deletes a message, bypassing holds, minimum keep and write-once. `reason` is `'illegal_content'` or `'court_order'`. |
| `eraseUser(userId, { mode? })` | `Promise<ErasureStatus>` | Starts an account erasure: `'anonymize'` (default) or `'delete'`. Answers `202`. |
| `getErasure(erasureId)` / `listUserErasures(userId)` | `Promise<ErasureStatus>` / `Promise<ErasureStatus[]>` | Live status: `in_progress`, `waiting` (with `blockedBy`) or `completed`. |
| `handleErasureWebhook(request)` / `handleErasureWebhookBody(body, signature, rawBody, { timestamp, signatureV2 })` | `Promise<Response>` / `Promise<WebhookResult>` | Verifies and handles dotwire's erasure notices, then calls `onErasure`. Requires `webhookSecret` (or `allowUnsignedWebhooks: true`). |
| `exportRoomHistory(roomId, asUserId, sink)` | `Promise<number>` | Writes the room's history as NDJSON, one message per call to `sink`, and returns the count. |
| `archiveAuditLog(sink, { afterId?, previousHash? })` | `Promise<{ count, lastId, lastHash }>` | Writes the audit log as NDJSON, recomputes each hash and throws on a break. Also writes checkpoints. Pass `previousHash` when `afterId` is above zero. |

When the server runs with `Dotwire:HubSend:Enabled=true`, `POST /rooms/{roomId}/messages`
shares one per-user send bucket with the hub `Send` method. The bucket is checked before the
role and membership checks (a malformed body still gets `400` first), and an exhausted bucket
returns `429` with `Retry-After`, surfaced as `DotwireApiError { kind: 'RateLimited', retryAfterMs }`.
Other errors keep their mapping: `403` `Forbidden`, `422` `PresendRejected`, `503` `Unavailable`.
`sendAsUser` never retries a `429` itself, and `Idempotency-Key` semantics are unchanged. This
SDK has no hub transport: hub sending is member-facing and lives in `@dotwire/client`
(`hubSend: true`), as with `Typing`.

Room ids must be UUIDs; anything else throws a `TypeError` before a request is built, and
`handleClientSend*` answers `400`. Webhook handlers verify `X-Dotwire-Signature-V2` over
`"<timestamp>.<raw body>"`, reject calls older than `webhookToleranceSeconds` (default 300) and
bodies over `maxWebhookBodyBytes` (default 1 MiB, `413`). The legacy body-only
`X-Dotwire-Signature` is accepted only with `allowLegacyWebhookSignatures: true`.

### `DotwireHostRoom`

| Method | Return Type | Description |
| :--- | :--- | :--- |
| `addMembers(userIds)` | `Promise<string[]>` | Authorizes users for this room. |
| `removeMember(userId)` | `Promise<void>` | Removes a user from this room. |
| `getMembers()` | `Promise<string[]>` | Lists all member user IDs in this room. |
| `getParticipants(asUserId)` | `Promise<string[]>` | Lists participant IDs using the named user's room access. |
| `sendSystemMessage(content, options?)` | `Promise<SendMessageResult>` | Publishes a host announcement into this room. |
| `sendAsUser(userId, content)` | `Promise<SendMessageResult>` | Sends as a room member. |
| `getMessages(options)` | `Promise<RoomHistoryResponse>` | Retrieves history as `options.asUserId`. |
| `emergencyDelete(seq, reason)` | `Promise<RedactMessageResponse>` | Emergency deletion of one message in this room. See "Compliance and erasure". |

---

## License

Apache-2.0
