import test from 'node:test';
import assert from 'node:assert/strict';
import type { RoomMessage } from '../src/types.js';
import { createTestClient, jsonResponse } from './test-helpers.js';

const ROOM_ID = '11111111-1111-1111-1111-111111111111';

test('room participants returns only the user IDs from the member endpoint', async (t) => {
  const { client } = createTestClient();
  t.mock.method(globalThis, 'fetch', async (input: string | URL | Request, init?: RequestInit) => {
    assert.equal(String(input), `https://dotwire.test/rooms/${ROOM_ID}/participants`);
    assert.equal(init?.method, 'GET');
    assert.equal(new Headers(init?.headers).get('Authorization'), 'Bearer test-token');
    return jsonResponse(['alice', 'bob']);
  });

  assert.deepEqual(await client.room(ROOM_ID).getParticipants(), ['alice', 'bob']);
});

test('subscribe({history}) returns the latest page and live delivery continues', async (t) => {
  const { client, fake } = createTestClient();

  t.mock.method(globalThis, 'fetch', async () =>
    jsonResponse({
      roomId: ROOM_ID,
      messages: [
        { seq: 1, time: '2026-01-01T00:00:00Z', senderId: 'alice', content: 'hi' },
        { seq: 2, time: '2026-01-01T00:00:01Z', senderId: 'bob', content: 'hey' }
      ],
      hasMore: false
    })
  );

  await client.connect();
  const room = client.room(ROOM_ID);

  const received: RoomMessage[] = [];
  room.onMessage((message) => received.push(message));

  const page = await room.subscribe({ history: 2 });

  assert.ok(page);
  assert.equal(page!.messages.length, 2);
  assert.equal(page!.hasMore, false);
  assert.deepEqual(
    received.map((m) => m.seq),
    [1, 2]
  );
  assert.ok(fake.invokeCalls.some((c) => c.method === 'Subscribe' && c.args[0] === ROOM_ID));

  // Live delivery continues after the history replay.
  fake.emit('ReceiveMessage', { roomId: ROOM_ID, seq: 3, senderId: 'alice', time: '2026-01-01T00:00:02Z', content: 'live' });
  assert.deepEqual(
    received.map((m) => m.seq),
    [1, 2, 3]
  );
});

test('dedupe: repeated seq (own echo / overlap) is delivered only once', async () => {
  const { client, fake } = createTestClient();
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();

  const received: RoomMessage[] = [];
  room.onMessage((message) => received.push(message));

  const message = { roomId: ROOM_ID, seq: 5, senderId: 'alice', time: '2026-01-01T00:00:00Z', content: 'hello' };
  fake.emit('ReceiveMessage', message);
  fake.emit('ReceiveMessage', message);
  fake.emit('ReceiveMessage', { ...message });

  assert.equal(received.length, 1);
  assert.equal(received[0].seq, 5);
});

test('message retraction notifies listeners', async () => {
  const { client, fake } = createTestClient();
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();

  const retracted: Array<{ roomId: string; seq: number }> = [];
  room.onMessageRetracted((payload) => retracted.push(payload));

  fake.emit('MessageRetracted', { roomId: ROOM_ID, seq: 7 });
  // Retraction for a different room must not reach this room's scoped listener.
  fake.emit('MessageRetracted', { roomId: '22222222-2222-2222-2222-222222222222', seq: 1 });

  assert.equal(retracted.length, 1);
  assert.equal(retracted[0].seq, 7);
});

test('membership revocation unsubscribes the room and blocks resubscribe on reconnect', async () => {
  const { client, fake } = createTestClient();
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();

  const revoked: Array<{ roomId: string; userId: string }> = [];
  room.onMembershipRevoked((payload) => revoked.push(payload));

  fake.emit('MembershipRevoked', { roomId: ROOM_ID, userId: 'alice' });
  assert.equal(revoked.length, 1);

  fake.invokeCalls.length = 0;
  await fake.simulateReconnected();

  assert.equal(
    fake.invokeCalls.filter((c) => c.method === 'Subscribe').length,
    0,
    'must not resubscribe a room the user was revoked from'
  );
});
