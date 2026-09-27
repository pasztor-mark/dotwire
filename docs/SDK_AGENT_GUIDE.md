# SDK guide for coding agents

Use this guide when adding dotwire chat to a host application. dotwire stores room membership and messages; the host application owns users, profiles, authentication, and the RS256 private key. Keep the private key on the server. The browser receives a short-lived dotwire token from an authenticated host endpoint.

## Choose the SDK

| SDK | Runs in | Use it for |
|---|---|---|
| `Dotwire.Host` | .NET host backend | Mint user tokens, grant roles and room membership, send host messages, read as a user, administer audit and retention |
| `@dotwire/host` | Node.js host backend | The same host operations from JavaScript or TypeScript |
| `@dotwire/client` | Browser or JavaScript client | Subscribe to rooms, send messages, load history and participants, show typing and presence |
| `@dotwire/react` | React client | Provider and hooks for messages, typing, presence, and connection state; use its underlying client for participants |

There is no .NET member client SDK in this repository. A .NET backend uses `Dotwire.Host`; a browser uses `@dotwire/client` or `@dotwire/react`. Pick one host SDK for the host's language. The host SDKs use dotwire's HTTP API and do not need direct database access.

## Integration order

1. Run the dotwire service with Postgres and NATS. Configure dotwire with the host's **public** signing key, issuer, and audience. Keep the corresponding private key in the host.
2. In the host, grant each user a `member` role and add them to rooms. Dotwire checks the role and room membership in its own tables; a token alone does not grant a room.
3. Expose an authenticated host endpoint that mints a dotwire token for the current host user. Derive the user ID from the host session, not a caller-supplied request body.
4. Point the member SDK at dotwire and at that token endpoint. Subscribe to a room and load history.
5. Fetch `GET /rooms/{roomId}/participants` through the SDK when the UI needs the current member IDs. Resolve usernames and avatars through a host-owned batch user lookup. Cache those profiles in the frontend according to the host's policy.

`GET /rooms/{roomId}/participants` returns a JSON array of user IDs, sorted by ID. It requires a valid token, a matching role in `user_roles`, and membership in `room_members`. An admin or auditor also needs room membership. The response contains no profile fields.

## `Dotwire.Host` (.NET backend)

Use [`Dotwire.Host/README.md`](../Dotwire.Host/README.md) for construction and configuration. The host can manage membership with its admin identity, then read participants as a specific room member:

```csharp
Guid roomId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
await client.SetUserRoleAsync("user-42", "member");
await client.AddRoomMembersAsync(roomId, ["user-42", "user-57"]);

string token = client.MintToken("user-42", role: "member");
IReadOnlyList<string> userIds = await client.GetRoomParticipantsAsync(roomId, "user-42");
```

`GetRoomMembersAsync(roomId)` is an **admin** operation for host management. `GetRoomParticipantsAsync(roomId, asUserId)` uses that user's room access. Pass the authenticated host user's ID for `asUserId` when serving a user request. The host SDK does not fetch usernames or avatars.

## `@dotwire/host` (Node.js backend)

Use [`packages/host/README.md`](../packages/host/README.md) for configuration. The current token method takes the user ID first and options second:

```ts
await host.setUserRole('user-42', 'member');
await host.addRoomMembers(roomId, ['user-42', 'user-57']);

const token = await host.mintToken('user-42', { role: 'member' });
const userIds = await host.getRoomParticipants(roomId, 'user-42');
```

`getRoomMembers(roomId)` uses the host's admin identity. `getRoomParticipants(roomId, asUserId)` uses the named user's room access. Keep token minting and membership changes in the host backend.

## `@dotwire/client` (JavaScript member client)

Use [`packages/client/README.md`](../packages/client/README.md) for connection and event lifecycle. The host token route should return `{ token: string }` for the current authenticated user:

```ts
const client = new DotwireClient({
  baseUrl: 'https://chat.example.com',
  tokenUrl: '/api/dotwire/token'
});
const room = client.room(roomId);

await room.subscribe({ history: 50 });
const userIds = await room.getParticipants();
const ack = await room.sendMessage('Hello');
```

Use `ack.seq` as the room message's ordering token. A send acknowledgement means JetStream accepted the message; history may lag briefly. `getParticipants()` is a membership list, not a presence snapshot. Presence events describe who is currently connected.

## `@dotwire/react` (React member client)

Use [`packages/react/README.md`](../packages/react/README.md) for `DotwireProvider`, `useRoom`, `useTyping`, and `usePresence`. `useRoom(roomId)` owns message subscription and history state. `@dotwire/react` has no separate participants hook; obtain the underlying `DotwireClient` with `useDotwireClient()` and call `client.room(roomId).getParticipants()` when needed. Resolve returned IDs through the host's profile API and cache them in app state.

Treat a participant list and `usePresence(roomId).present` differently: the first is current room membership; the second is live connection activity. Refresh the participant list after the host changes membership or when opening the room.

## Access and ownership checks

- Keep the signing private key and `Dotwire.Host` / `@dotwire/host` on the backend.
- A host profile lookup should apply the host's own visibility rules. Dotwire does not know whether one user may see another user's username or avatar.
- Never treat the `dw:role` claim as the sole grant. Dotwire cross-checks it against `user_roles`, then checks `room_members` for room reads.
- Use admin membership methods to change membership. Use member participant methods for a member-facing list.
- Preserve `room_id` in room queries and use the read data source for reads.
