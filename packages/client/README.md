# @dotwire/client

For coding agents adding dotwire to an application, see the [SDK integration guide](../../docs/SDK_AGENT_GUIDE.md).

Official TypeScript/JavaScript client SDK for **dotwire**, the high-throughput, self-hosted real-time messaging infrastructure.

[![npm version](https://img.shields.io/npm/v/@dotwire/client.svg)](https://www.npmjs.com/package/@dotwire/client)
[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://github.com/pasztor-mark/dotwire)

## Features

- **Scoped Room Handles:** Fluid, object-oriented API via `client.room(roomId)`.
- **Real-Time SignalR & NATS Fanout:** Sub-millisecond latency for message delivery, ephemeral typing, and presence.
- **Intelligent Typing Controller:** Built-in keystroke throttling (`1000ms`), natural auto-stop timeouts (`3000ms`), and auto-expiring typing listeners (`3500ms`).
- **Resilient Reconnection:** Automatic SignalR reconnect with automatic room group resubscription.
- **Natural Request Timeouts:** Built-in `AbortSignal.timeout` protection on all network requests.
- **System & Bot Message Filtering:** Dedicated listeners for system announcements vs. user chat.
- **Zero-Dependency Core:** Light bundle size built for browsers, Next.js, React, Vue, Svelte, and Node.js.

---

## Installation

```bash
npm install @dotwire/client
# or
pnpm add @dotwire/client
# or
yarn add @dotwire/client
```

---

## Quickstart

```typescript
import { DotwireClient } from '@dotwire/client';

// 1. Initialize client with either tokenUrl (recommended: the SDK caches and
//    refreshes the token itself) or a getAccessToken escape hatch.
const client = new DotwireClient({
  baseUrl: 'https://chat.yourdomain.com',
  tokenUrl: '/api/auth/token', // GET, credentials included, expects `{ token }`
  automaticReconnect: true,
  autoStopTypingMs: 3000,
  typingThrottleMs: 1000
});

// 2. Obtain a scoped room handle
const room = client.room('a0000000-0000-0000-0000-000000000001');

// 3. Subscribe to real-time events (pass `{ history: 50 }` to also fetch and
//    deliver the latest page of history as part of subscribing)
await room.subscribe();

// 4. Listen for incoming messages
const unsubMessage = room.onMessage((message) => {
  console.log(`[${message.time}] #${message.seq} ${message.senderId}: ${message.content}`);
});

// 5. Send a message
const ack = await room.sendMessage('Hello, world!');
console.log(`Message persisted at stream sequence ${ack.seq}`);

// 6. Fetch historical messages
const history = await room.getHistory({ limit: 50 });
console.log(`Loaded ${history.messages.length} messages (hasMore: ${history.hasMore})`);

// Room membership is a list of user IDs. Resolve profiles in the host app.
const participantIds = await room.getParticipants();

// 7. Cleanup when finished
unsubMessage();
room.dispose();
await client.disconnect();
```

---

## Guide & Usage Examples

### 1. Scoped Room Handle (`DotwireRoomHandle`)

Working with rooms is centered around `DotwireRoomHandle`:

```typescript
const room = client.room('room-uuid');

// Connect & join group
await room.subscribe();

// Leave room
await room.unsubscribe();

// Clean up all room listeners and auto-unsubscribe
room.dispose();
```

---

### 2. Message History & Gap-Fill Replay

Fetch decrypted history from PostgreSQL/TimescaleDB with monotonic sequence pagination:

```typescript
// Fetch latest 50 messages
const latest = await room.getHistory({ limit: 50 });

// Paginate older messages before a sequence number
const older = await room.getHistory({
  beforeSeq: latest.messages[0].seq,
  limit: 50
});

// Replay missed messages after a reconnection
const missed = await room.getHistory({
  afterSeq: lastKnownSeq,
  limit: 100
});
```

---

### 3. Real-Time Typing Indicators

The SDK includes intelligent throttling and auto-expiration so your UI never gets stuck showing a user typing:

```typescript
// Sending typing events from an input field
function onUserKeystroke() {
  // Automatically throttles network dispatch and auto-stops after 3s of inactivity
  room.notifyTyping();
}

function onUserSubmit() {
  // Immediately stops typing
  room.stopTyping();
}

// Receiving typing notifications with auto-expiry
const unsubTyping = room.onTypingWithExpiry((event) => {
  if (event.isTyping) {
    showTypingIndicator(event.userId);
  } else {
    hideTypingIndicator(event.userId);
  }
}, 3500);
```

---

### 4. User Presence (Joins & Leaves)

Tracks real-time participant changes coalesced by Dotwire's backend:

```typescript
const unsubPresence = room.onPresence((delta) => {
  console.log('Joined:', delta.joined);
  console.log('Left:', delta.left);
});
```

---

### 5. System & Bot Messages

Separate automated notifications and maintenance notices from regular user chat:

```typescript
// Listen ONLY for system announcements
room.onSystemMessage((message) => {
  showSystemBanner(message.content);
});

// Listen ONLY for standard user messages
room.onUserMessage((message) => {
  appendUserChatMessage(message);
});
```

---

### 6. React / Next.js Hook Pattern

Here is a recommended React hook pattern:

```typescript
import { useEffect, useState, useRef } from 'react';
import { DotwireClient, DotwireRoomHandle, ConnectionState, RoomMessage } from '@dotwire/client';

export function useChatRoom(roomId: string, fetchToken: () => Promise<string>) {
  const [messages, setMessages] = useState<RoomMessage[]>([]);
  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
  const [typingUsers, setTypingUsers] = useState<string[]>([]);
  const roomRef = useRef<DotwireRoomHandle | null>(null);

  useEffect(() => {
    let active = true;
    const client = new DotwireClient({
      baseUrl: process.env.NEXT_PUBLIC_DOTWIRE_URL || '',
      getAccessToken: fetchToken,
      automaticReconnect: true
    });

    const room = client.room(roomId);
    roomRef.current = room;

    client.onStateChange((state) => {
      if (active) setConnectionState(state);
    });

    room.onMessage((message) => {
      if (!active) return;
      setMessages((prev) => [...prev, message]);
    });

    room.onTypingWithExpiry((event) => {
      if (!active) return;
      setTypingUsers((prev) =>
        event.isTyping
          ? (prev.includes(event.userId) ? prev : [...prev, event.userId])
          : prev.filter((id) => id !== event.userId)
      );
    });

    async function init() {
      // `history` fetches the latest page and delivers each message through the
      // onMessage listener above, so there is no separate setMessages call here.
      await room.subscribe({ history: 50 });
    }

    init();

    return () => {
      active = false;
      room.dispose();
      client.dispose();
    };
  }, [roomId, fetchToken]);

  return {
    messages,
    connectionState,
    typingUsers,
    sendMessage: (content: string) => roomRef.current?.sendMessage(content),
    notifyTyping: () => roomRef.current?.notifyTyping(),
    stopTyping: () => roomRef.current?.stopTyping()
  };
}
```

---

## API Reference

### `DotwireClient`

| Method / Property | Return Type | Description |
| :--- | :--- | :--- |
| `new DotwireClient(options)` | `DotwireClient` | Initializes client with baseUrl, token factory, and options. |
| `room(roomId)` | `DotwireRoomHandle` | Returns a scoped room handle for the specified room UUID. |
| `connect()` | `Promise<void>` | Manually initiates the SignalR connection. |
| `disconnect()` | `Promise<void>` | Closes the connection and leaves all rooms. |
| `getConnectionState()` | `ConnectionState` | Returns `'connected'`, `'connecting'`, `'reconnecting'`, or `'disconnected'`. |
| `onMessage(handler)` | `() => void` | Global listener for any message on subscribed rooms. Delivers `RoomMessage` (has `seq`; `replayed: true` during reconnect gap-fill). |
| `onSystemMessage(handler)` | `() => void` | Global listener for system messages only. |
| `onUserMessage(handler)` | `() => void` | Global listener for user messages only. |
| `onMessageRetracted(handler)` | `() => void` | Global listener for redacted/retracted messages. |
| `onMembershipRevoked(handler)` | `() => void` | Global listener fired when the user's membership in a room is revoked; the room is auto-unsubscribed. |
| `onTyping(handler)` | `() => void` | Global listener for user typing notifications. |
| `onPresence(handler)` | `() => void` | Global listener for presence changes. |
| `onStateChange(handler)` | `() => void` | Subscribes to connection state transitions. |
| `dispose()` | `void` | Cleans up all active room handles and disconnects. |

Per-room seq dedupe (a bounded set of the last 1000 seen sequence numbers) and reconnect
gap-fill (paging `getHistory({afterSeq, limit: gapFillPageSize})` after an automatic
reconnect) happen internally; consumers just see `onMessage` fire once per new seq, with
`replayed: true` on gap-fill deliveries.

### `DotwireRoomHandle`

| Method | Return Type | Description |
| :--- | :--- | :--- |
| `subscribe(options?)` | `Promise<HistoryPage \| undefined>` | Subscribes to room events. With `{ history: n }`, also fetches and delivers the latest `n` messages, returning that page. |
| `unsubscribe()` | `Promise<void>` | Leaves the real-time group. |
| `sendMessage(content)` | `Promise<SendAck>` | Sends a message to the room. Returns stream sequence and time. |
| `getHistory(options)` | `Promise<HistoryPage>` | Retrieves paginated message history (`{ messages: RoomMessage[], hasMore }`). |
| `notifyTyping()` | `Promise<void>` | Throttled typing trigger with automatic 3s auto-stop timer. |
| `startTyping()` / `stopTyping()` | `Promise<void>` | Explicit typing state modifiers. |
| `onMessage(handler)` | `() => void` | Room-scoped message listener. Returns unsubscribe function. |
| `onSystemMessage(handler)` | `() => void` | Room-scoped system message listener. |
| `onUserMessage(handler)` | `() => void` | Room-scoped user message listener. |
| `onMessageRetracted(handler)` | `() => void` | Room-scoped retraction listener. |
| `onMembershipRevoked(handler)` | `() => void` | Room-scoped revocation listener. |
| `onTyping(handler)` | `() => void` | Room-scoped typing indicator listener. |
| `onTypingWithExpiry(handler, ms)` | `() => void` | Room-scoped typing listener that auto-resets after inactivity. |
| `onPresence(handler)` | `() => void` | Room-scoped presence update listener. |
| `dispose()` | `void` | Cancels timers, unbinds listeners, and leaves the room. |

### Errors

Failed requests reject with a `DotwireClientError { status, kind, code?, reason?, retryAfterMs? }`,
where `kind` is one of `BadRequest`, `Unauthorized`, `Forbidden`, `NotFound`, `PresendRejected`,
`RateLimited`, `Unavailable`, or `Unknown` — the same kinds used by `Dotwire.Host` and
`@dotwire/host`.

---

## License

Apache-2.0
