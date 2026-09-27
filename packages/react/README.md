# @dotwire/react

For coding agents adding dotwire to an application, see the [SDK integration guide](../../docs/SDK_AGENT_GUIDE.md).

React hooks and a provider for [`@dotwire/client`](../client), the TypeScript/JavaScript SDK
for **dotwire**, the high-throughput, self-hosted real-time messaging infrastructure.

[![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)](https://github.com/pasztor-mark/dotwire)

## Installation

```bash
npm install @dotwire/react @dotwire/client react
```

`react` (>= 18) and `@dotwire/client` are peer dependencies — install them alongside this
package. Every export in this package is client-only (it uses hooks and context); the bundle
carries a `"use client"` banner for React Server Components frameworks.

## Quickstart

```tsx
import { DotwireProvider, useRoom, useTyping, usePresence, useConnectionState } from '@dotwire/react';

const dotwireOptions = {
  baseUrl: 'https://chat.yourdomain.com',
  tokenUrl: '/api/auth/token'
};

function App() {
  return (
    <DotwireProvider options={dotwireOptions}>
      <Room roomId="a0000000-0000-0000-0000-000000000001" />
    </DotwireProvider>
  );
}

function Room({ roomId }: { roomId: string }) {
  const connectionState = useConnectionState();
  const { messages, status, hasMore, loadOlder, send } = useRoom(roomId, { history: 50 });
  const { typingUsers, notifyTyping, stopTyping } = useTyping(roomId);
  const { present } = usePresence(roomId);

  return (
    <div>
      <p>Connection: {connectionState}</p>
      <p>Room: {status}</p>
      {hasMore && <button onClick={loadOlder}>Load older</button>}
      <ul>
        {messages.map((m) => (
          <li key={m.seq}>
            #{m.seq} {m.senderId}: {m.content}
          </li>
        ))}
      </ul>
      <p>Typing: {typingUsers.join(', ') || '—'}</p>
      <p>Present: {present.join(', ') || '—'}</p>
      <input
        onChange={() => notifyTyping()}
        onBlur={() => stopTyping()}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            void send(e.currentTarget.value);
            e.currentTarget.value = '';
          }
        }}
      />
    </div>
  );
}
```

## `<DotwireProvider>`

```tsx
<DotwireProvider options={dotwireOptions}>...</DotwireProvider>
// or, if you already manage a DotwireClient yourself:
<DotwireProvider client={existingClient}>...</DotwireProvider>
```

- Give it `options` (a `DotwireClientOptions`, same shape as `new DotwireClient(options)`) and
  it constructs and disposes the client for you. Construction happens inside a `useEffect`, so
  it is safe to render `DotwireProvider` on the server (`react-dom/server`) — nothing
  browser-only runs during the render pass itself. Pass a stable `options` reference (e.g. a
  module-level constant, or memoized with `useMemo`/`useState`) — a new object identity on
  every render tears down and rebuilds the client.
- Give it `client` instead and it never constructs or disposes anything — you own that
  instance's lifecycle (including calling `client.dispose()` yourself).
- `useDotwireClient()` returns `null` until the provider's effect has produced a client (always
  the case during SSR, and briefly on first client-side render).

## Hooks

- **`useDotwireClient()`** → `DotwireClient | null`.
  Use `client.room(roomId).getParticipants()` to fetch member IDs, then resolve names and avatars through the host app.
- **`useConnectionState()`** → the client's live `ConnectionState`
  (`'disconnected' | 'connecting' | 'connected' | 'reconnecting' | 'disconnecting'`).
- **`useRoom(roomId, { history = 50 })`** → `{ messages, status, error, hasMore, loadOlder, send }`.
  Subscribes for the lifetime of the component, loads the initial history page, and applies
  live messages, retractions, and revocation. `messages` is always sorted by `seq` and deduped.
  `status` starts `'connecting'`, becomes `'ready'` once the initial subscribe resolves,
  `'revoked'` on membership revocation, or `'error'` if subscribe/send throws. `loadOlder()`
  pages backwards with `beforeSeq` and prepends the results; `hasMore` reflects whether an
  older page might still exist. `send(content)` wraps `sendMessage`.
- **`useTyping(roomId)`** → `{ typingUsers, notifyTyping, stopTyping }`. A user drops out of
  `typingUsers` either on an explicit "stopped typing" notification or after ~3.5s of silence
  (time-based expiry, checked every second).
- **`usePresence(roomId)`** → `{ present }`, accumulated from `joined`/`left` deltas since this
  hook mounted. **There is no presence snapshot endpoint on the server** — a component that
  mounts this hook after other members already joined will not see them in `present` until the
  *next* delta for that room (the next join or leave).

## Architecture

All state transitions (new message, retraction, revocation, typing start/stop/expiry, presence
deltas) live in pure, framework-free reducer functions in `src/reducers.ts` — each is unit
tested directly with Node's test runner (`test/reducers.test.ts`), with no React or DOM
involved. The hooks in `src/hooks.ts` are thin `useReducer` wrappers that wire the underlying
`DotwireClient`/room event callbacks to `dispatch` calls.

`useRoom`, `useTyping`, and `usePresence` can be mounted together for the same `roomId` (e.g. a
message list plus a typing indicator). `DotwireClient.subscribe`/`unsubscribe` track hub-group
membership per room without a refcount, so this package refcounts "leave the room" intent
itself (`src/subscription.ts`) — the underlying hub group is only actually left once every hook
watching that room has unmounted.

There is a `react-dom/server` smoke test (`test/ssr.test.tsx`) asserting `DotwireProvider`
renders without throwing when no browser APIs are available. Full DOM-based hook tests (e.g.
with `jsdom`) are intentionally not included — add a DOM testing library of your choice if you
want them.

## License

Apache-2.0
