/**
 * Pure state-transition functions backing `useRoom`, `useTyping`, and `usePresence`
 * (spec §7). None of these import React; each is unit-tested directly with Node's
 * test runner in `test/reducers.test.ts`. The hooks in `src/hooks.ts` are thin
 * `useReducer` wrappers around these.
 */
import type { HistoryPage, RoomMessage } from '@dotwire/client';

// ---------------------------------------------------------------------------
// Room messages
// ---------------------------------------------------------------------------

export type RoomStatus = 'connecting' | 'ready' | 'revoked' | 'error';

export interface RoomMessagesState {
  messages: RoomMessage[];
  status: RoomStatus;
  error: unknown;
  hasMore: boolean;
}

export type RoomMessagesAction =
  | { type: 'reset' }
  | { type: 'subscribed'; page: HistoryPage }
  | { type: 'message'; message: RoomMessage }
  | { type: 'retracted'; seq: number }
  | { type: 'revoked' }
  | { type: 'error'; error: unknown }
  | { type: 'olderLoaded'; page: HistoryPage };

export const initialRoomMessagesState: RoomMessagesState = {
  messages: [],
  status: 'connecting',
  error: null,
  hasMore: false
};

/**
 * Merges `incoming` into `existing`, deduping by `seq` (last write for a given seq
 * wins) and returning the result sorted ascending by `seq` (spec §6, §7: "consumers
 * order by seq themselves").
 */
function mergeMessages(existing: RoomMessage[], incoming: RoomMessage[]): RoomMessage[] {
  if (incoming.length === 0) return existing;
  const bySeq = new Map<number, RoomMessage>();
  for (const message of existing) bySeq.set(message.seq, message);
  for (const message of incoming) bySeq.set(message.seq, message);
  return Array.from(bySeq.values()).sort((a, b) => a.seq - b.seq);
}

export function roomMessagesReducer(
  state: RoomMessagesState,
  action: RoomMessagesAction
): RoomMessagesState {
  switch (action.type) {
    case 'reset':
      return initialRoomMessagesState;
    case 'subscribed':
      return {
        messages: mergeMessages(state.messages, action.page.messages),
        status: 'ready',
        error: null,
        hasMore: action.page.hasMore
      };
    case 'message':
      return { ...state, messages: mergeMessages(state.messages, [action.message]) };
    case 'retracted':
      return { ...state, messages: state.messages.filter((m) => m.seq !== action.seq) };
    case 'revoked':
      return { ...state, status: 'revoked' };
    case 'error':
      return { ...state, status: 'error', error: action.error };
    case 'olderLoaded':
      return {
        ...state,
        messages: mergeMessages(state.messages, action.page.messages),
        hasMore: action.page.hasMore
      };
    default:
      return state;
  }
}

// ---------------------------------------------------------------------------
// Typing
// ---------------------------------------------------------------------------

export interface TypingEntry {
  updatedAt: number;
}

/** Keyed by userId; a key is present only while that user is considered typing. */
export type TypingState = Record<string, TypingEntry>;

export type TypingAction =
  | { type: 'notification'; userId: string; isTyping: boolean; now: number }
  | { type: 'expire'; now: number; ttlMs: number };

export const initialTypingState: TypingState = {};

export function typingReducer(state: TypingState, action: TypingAction): TypingState {
  switch (action.type) {
    case 'notification': {
      if (action.isTyping) {
        return { ...state, [action.userId]: { updatedAt: action.now } };
      }
      if (!(action.userId in state)) return state;
      const next = { ...state };
      delete next[action.userId];
      return next;
    }
    case 'expire': {
      let changed = false;
      const next: TypingState = {};
      for (const [userId, entry] of Object.entries(state)) {
        if (action.now - entry.updatedAt < action.ttlMs) {
          next[userId] = entry;
        } else {
          changed = true;
        }
      }
      return changed ? next : state;
    }
    default:
      return state;
  }
}

export function typingUsersOf(state: TypingState): string[] {
  return Object.keys(state);
}

// ---------------------------------------------------------------------------
// Presence
// ---------------------------------------------------------------------------

/** Sorted, deduped user ids accumulated from deltas since mount (spec §7: no snapshot endpoint). */
export type PresenceState = string[];

export type PresenceAction = { type: 'delta'; joined: string[]; left: string[] };

export const initialPresenceState: PresenceState = [];

export function presenceReducer(state: PresenceState, action: PresenceAction): PresenceState {
  const set = new Set(state);
  for (const id of action.left) set.delete(id);
  for (const id of action.joined) set.add(id);
  return Array.from(set).sort();
}
