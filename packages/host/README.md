# @dotwire/host

For coding agents adding dotwire to an application, see the [SDK integration guide](../../docs/SDK_AGENT_GUIDE.md).

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
| `sendAsUser(roomId, userId, content)` | `Promise<SendMessageResult>` | Sends as a room member. |
| `sendSystemMessage(roomId, content, options?)` | `Promise<SendMessageResult>` | Sends a host announcement to a room. |
| `getMessages(roomId, options)` | `Promise<RoomHistoryResponse>` | Queries history as `options.asUserId`. |

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

---

## License

Apache-2.0
