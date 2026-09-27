import { test } from 'node:test';
import assert from 'node:assert/strict';
import type { HistoryPage, RoomMessage } from '@dotwire/client';
import {
  initialPresenceState,
  initialRoomMessagesState,
  initialTypingState,
  presenceReducer,
  roomMessagesReducer,
  typingReducer,
  typingUsersOf
} from '../src/reducers.js';

function message(seq: number, overrides: Partial<RoomMessage> = {}): RoomMessage {
  return {
    roomId: 'room-1',
    seq,
    senderId: 'user-1',
    time: new Date(seq * 1000).toISOString(),
    content: `message ${seq}`,
    ...overrides
  };
}

function page(messages: RoomMessage[], hasMore = false): HistoryPage {
  return { messages, hasMore };
}

// --- roomMessagesReducer -----------------------------------------------------

test('roomMessagesReducer: initial state is connecting with no messages', () => {
  assert.deepEqual(initialRoomMessagesState, {
    messages: [],
    status: 'connecting',
    error: null,
    hasMore: false
  });
});

test('roomMessagesReducer: subscribed sets messages sorted by seq and status ready', () => {
  const state = roomMessagesReducer(
    initialRoomMessagesState,
    { type: 'subscribed', page: page([message(3), message(1), message(2)], true) }
  );
  assert.deepEqual(state.messages.map((m) => m.seq), [1, 2, 3]);
  assert.equal(state.status, 'ready');
  assert.equal(state.hasMore, true);
  assert.equal(state.error, null);
});

test('roomMessagesReducer: message inserts sorted and dedupes by seq', () => {
  let state = roomMessagesReducer(initialRoomMessagesState, {
    type: 'subscribed',
    page: page([message(1), message(2)])
  });
  state = roomMessagesReducer(state, { type: 'message', message: message(4) });
  state = roomMessagesReducer(state, { type: 'message', message: message(3) });
  // A duplicate delivery of an already-known seq (e.g. sender's own echo) must not create
  // a second entry.
  state = roomMessagesReducer(state, { type: 'message', message: message(2, { content: 'dup' }) });

  assert.deepEqual(state.messages.map((m) => m.seq), [1, 2, 3, 4]);
  assert.equal(state.messages.length, 4);
});

test('roomMessagesReducer: retracted removes the matching seq only', () => {
  let state = roomMessagesReducer(initialRoomMessagesState, {
    type: 'subscribed',
    page: page([message(1), message(2), message(3)])
  });
  state = roomMessagesReducer(state, { type: 'retracted', seq: 2 });
  assert.deepEqual(state.messages.map((m) => m.seq), [1, 3]);
});

test('roomMessagesReducer: revoked sets status without touching messages', () => {
  let state = roomMessagesReducer(initialRoomMessagesState, {
    type: 'subscribed',
    page: page([message(1)])
  });
  state = roomMessagesReducer(state, { type: 'revoked' });
  assert.equal(state.status, 'revoked');
  assert.deepEqual(state.messages.map((m) => m.seq), [1]);
});

test('roomMessagesReducer: error sets status and error payload', () => {
  const err = new Error('boom');
  const state = roomMessagesReducer(initialRoomMessagesState, { type: 'error', error: err });
  assert.equal(state.status, 'error');
  assert.equal(state.error, err);
});

test('roomMessagesReducer: olderLoaded prepends without duplicating overlap and updates hasMore', () => {
  let state = roomMessagesReducer(initialRoomMessagesState, {
    type: 'subscribed',
    page: page([message(5), message(6)], false)
  });
  state = roomMessagesReducer(state, {
    type: 'olderLoaded',
    page: page([message(3), message(4), message(5)], true)
  });
  assert.deepEqual(state.messages.map((m) => m.seq), [3, 4, 5, 6]);
  assert.equal(state.hasMore, true);
});

test('roomMessagesReducer: reset returns to initial state', () => {
  let state = roomMessagesReducer(initialRoomMessagesState, {
    type: 'subscribed',
    page: page([message(1)])
  });
  state = roomMessagesReducer(state, { type: 'reset' });
  assert.deepEqual(state, initialRoomMessagesState);
});

// --- typingReducer -------------------------------------------------------------

test('typingReducer: notification with isTyping=true adds the user', () => {
  const state = typingReducer(initialTypingState, {
    type: 'notification',
    userId: 'alice',
    isTyping: true,
    now: 1000
  });
  assert.deepEqual(typingUsersOf(state), ['alice']);
});

test('typingReducer: notification with isTyping=false removes the user', () => {
  let state = typingReducer(initialTypingState, {
    type: 'notification',
    userId: 'alice',
    isTyping: true,
    now: 1000
  });
  state = typingReducer(state, { type: 'notification', userId: 'alice', isTyping: false, now: 1200 });
  assert.deepEqual(typingUsersOf(state), []);
});

test('typingReducer: notification for an already-stopped user is a no-op (same reference)', () => {
  const state = typingReducer(initialTypingState, {
    type: 'notification',
    userId: 'alice',
    isTyping: false,
    now: 1000
  });
  assert.equal(state, initialTypingState);
});

test('typingReducer: expire removes entries older than ttlMs and keeps fresh ones', () => {
  let state = typingReducer(initialTypingState, { type: 'notification', userId: 'alice', isTyping: true, now: 0 });
  state = typingReducer(state, { type: 'notification', userId: 'bob', isTyping: true, now: 900 });

  state = typingReducer(state, { type: 'expire', now: 1000, ttlMs: 3500 });
  assert.deepEqual(typingUsersOf(state).sort(), ['alice', 'bob']);

  state = typingReducer(state, { type: 'expire', now: 3600, ttlMs: 3500 });
  assert.deepEqual(typingUsersOf(state), ['bob']);

  state = typingReducer(state, { type: 'expire', now: 4500, ttlMs: 3500 });
  assert.deepEqual(typingUsersOf(state), []);
});

test('typingReducer: expire with nothing to expire returns the same reference', () => {
  const state = typingReducer(initialTypingState, { type: 'notification', userId: 'alice', isTyping: true, now: 0 });
  const next = typingReducer(state, { type: 'expire', now: 10, ttlMs: 3500 });
  assert.equal(next, state);
});

// --- presenceReducer -------------------------------------------------------------

test('presenceReducer: initial state is empty', () => {
  assert.deepEqual(initialPresenceState, []);
});

test('presenceReducer: joined accumulates, sorted and deduped', () => {
  let state = presenceReducer(initialPresenceState, { type: 'delta', joined: ['bob', 'alice'], left: [] });
  state = presenceReducer(state, { type: 'delta', joined: ['alice', 'carol'], left: [] });
  assert.deepEqual(state, ['alice', 'bob', 'carol']);
});

test('presenceReducer: left removes only the departed users', () => {
  let state = presenceReducer(initialPresenceState, { type: 'delta', joined: ['alice', 'bob', 'carol'], left: [] });
  state = presenceReducer(state, { type: 'delta', joined: [], left: ['bob'] });
  assert.deepEqual(state, ['alice', 'carol']);
});

test('presenceReducer: a delta can join and leave different users at once', () => {
  let state = presenceReducer(initialPresenceState, { type: 'delta', joined: ['alice'], left: [] });
  state = presenceReducer(state, { type: 'delta', joined: ['bob'], left: ['alice'] });
  assert.deepEqual(state, ['bob']);
});
