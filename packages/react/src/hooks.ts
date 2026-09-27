import { useCallback, useContext, useEffect, useReducer, useRef, useState } from 'react';
import type { ConnectionState, DotwireClient, RoomMessage, SendAck } from '@dotwire/client';
import { DotwireContext } from './provider.js';
import {
  initialPresenceState,
  initialRoomMessagesState,
  initialTypingState,
  presenceReducer,
  roomMessagesReducer,
  typingReducer,
  typingUsersOf,
  type RoomMessagesState,
  type RoomStatus
} from './reducers.js';
import { releaseRoom, retainRoom } from './subscription.js';

/** The `DotwireClient` from the nearest `DotwireProvider`, or `null` before it has mounted. */
export function useDotwireClient(): DotwireClient | null {
  return useContext(DotwireContext);
}

/** The client's live `ConnectionState`, re-rendering on every `onStateChange` event. */
export function useConnectionState(): ConnectionState {
  const client = useDotwireClient();
  const [state, setState] = useState<ConnectionState>(() => client?.getConnectionState() ?? 'disconnected');

  useEffect(() => {
    if (!client) {
      setState('disconnected');
      return undefined;
    }
    setState(client.getConnectionState());
    return client.onStateChange(setState);
  }, [client]);

  return state;
}

export interface UseRoomOptions {
  /** How many of the latest messages to load on subscribe. Default 50. */
  history?: number;
}

export interface UseRoomResult {
  messages: RoomMessage[];
  status: RoomStatus;
  error: unknown;
  hasMore: boolean;
  loadOlder: () => Promise<void>;
  send: (content: string) => Promise<SendAck>;
}

/**
 * Subscribes to `roomId` for as long as the component is mounted: loads the initial history
 * page, applies live messages/retractions/revocation, and exposes `loadOlder`/`send` (spec
 * §7). All state transitions are delegated to `roomMessagesReducer` (`src/reducers.ts`).
 */
export function useRoom(roomId: string, options: UseRoomOptions = {}): UseRoomResult {
  const history = options.history ?? 50;
  const client = useDotwireClient();
  const [state, dispatch] = useReducer(roomMessagesReducer, initialRoomMessagesState);
  const stateRef = useRef<RoomMessagesState>(state);
  stateRef.current = state;

  useEffect(() => {
    if (!client) return undefined;

    dispatch({ type: 'reset' });
    const room = client.room(roomId);
    retainRoom(client, roomId);
    let cancelled = false;

    const unsubscribers = [
      room.onMessage((message) => dispatch({ type: 'message', message })),
      room.onMessageRetracted(({ seq }) => dispatch({ type: 'retracted', seq })),
      room.onMembershipRevoked(() => dispatch({ type: 'revoked' }))
    ];

    room
      .subscribe({ history })
      .then((page) => {
        if (cancelled) return;
        dispatch({ type: 'subscribed', page: page ?? { messages: [], hasMore: false } });
      })
      .catch((error: unknown) => {
        if (!cancelled) dispatch({ type: 'error', error });
      });

    return () => {
      cancelled = true;
      for (const unsub of unsubscribers) unsub();
      // Only the last interested hook for this (client, roomId) actually leaves the hub
      // group — see `src/subscription.ts`. We deliberately do NOT call `room.dispose()`
      // here: it would unconditionally unsubscribe, which is exactly what the refcount
      // guards against when `useTyping`/`usePresence` share the same room.
      if (releaseRoom(client, roomId)) {
        client.unsubscribe(roomId).catch(() => {});
      }
    };
  }, [client, roomId, history]);

  const loadOlder = useCallback(async () => {
    if (!client) return;
    const oldest = stateRef.current.messages[0];
    if (!oldest) return;
    try {
      const page = await client.getHistory(roomId, { beforeSeq: oldest.seq, limit: history });
      dispatch({ type: 'olderLoaded', page });
    } catch (error) {
      dispatch({ type: 'error', error });
    }
  }, [client, roomId, history]);

  const send = useCallback(
    async (content: string): Promise<SendAck> => {
      if (!client) {
        throw new Error('Dotwire client is not ready yet.');
      }
      try {
        return await client.sendMessage(roomId, content);
      } catch (error) {
        dispatch({ type: 'error', error });
        throw error;
      }
    },
    [client, roomId]
  );

  return {
    messages: state.messages,
    status: state.status,
    error: state.error,
    hasMore: state.hasMore,
    loadOlder,
    send
  };
}

export interface UseTypingResult {
  /** User ids currently considered typing (expired entries are removed automatically). */
  typingUsers: string[];
  notifyTyping: () => Promise<void>;
  stopTyping: () => Promise<void>;
}

/** How long a typing notification stays valid absent a follow-up (spec §7: "with expiry"). */
const TYPING_EXPIRY_MS = 3500;
const TYPING_EXPIRY_CHECK_INTERVAL_MS = 1000;

/**
 * Tracks who is typing in `roomId`. Expiry is time-based (a user who goes silent without an
 * explicit stop notification drops out after `TYPING_EXPIRY_MS`) and driven by `typingReducer`,
 * which takes an explicit `now` so it is unit-testable without timers.
 */
export function useTyping(roomId: string): UseTypingResult {
  const client = useDotwireClient();
  const [state, dispatch] = useReducer(typingReducer, initialTypingState);
  const roomHandleRef = useRef<ReturnType<DotwireClient['room']> | null>(null);

  useEffect(() => {
    if (!client) return undefined;

    const room = client.room(roomId);
    roomHandleRef.current = room;
    retainRoom(client, roomId);
    // Typing/presence delivery requires hub group membership, same as message delivery.
    room.subscribe().catch(() => {
      // Best-effort: if this never resolves, no typing events will arrive for this room;
      // the caller can observe that via useConnectionState/useRoom's own error state.
    });

    const unsub = room.onTyping((notification) => {
      dispatch({ type: 'notification', userId: notification.userId, isTyping: notification.isTyping, now: Date.now() });
    });

    const interval = setInterval(() => {
      dispatch({ type: 'expire', now: Date.now(), ttlMs: TYPING_EXPIRY_MS });
    }, TYPING_EXPIRY_CHECK_INTERVAL_MS);

    return () => {
      unsub();
      clearInterval(interval);
      roomHandleRef.current = null;
      if (releaseRoom(client, roomId)) {
        client.unsubscribe(roomId).catch(() => {});
      }
    };
  }, [client, roomId]);

  const notifyTyping = useCallback(async () => {
    await roomHandleRef.current?.notifyTyping();
  }, []);

  const stopTyping = useCallback(async () => {
    await roomHandleRef.current?.stopTyping();
  }, []);

  return { typingUsers: typingUsersOf(state), notifyTyping, stopTyping };
}

export interface UsePresenceResult {
  /** User ids present, accumulated from deltas since this hook mounted. */
  present: string[];
}

/**
 * Accumulates presence deltas for `roomId` since mount (spec §7). There is no presence
 * snapshot endpoint on the server, so a component that mounts this hook after other members
 * already joined will not see them in `present` until the next `PresenceUpdated` delta for
 * this room (e.g. the next member to join or leave).
 */
export function usePresence(roomId: string): UsePresenceResult {
  const client = useDotwireClient();
  const [state, dispatch] = useReducer(presenceReducer, initialPresenceState);

  useEffect(() => {
    if (!client) return undefined;

    const room = client.room(roomId);
    retainRoom(client, roomId);
    room.subscribe().catch(() => {
      // Best-effort, same rationale as useTyping.
    });

    const unsub = room.onPresence((delta) => {
      dispatch({ type: 'delta', joined: delta.joined, left: delta.left });
    });

    return () => {
      unsub();
      if (releaseRoom(client, roomId)) {
        client.unsubscribe(roomId).catch(() => {});
      }
    };
  }, [client, roomId]);

  return { present: state };
}
