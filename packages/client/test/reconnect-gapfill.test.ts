import test from 'node:test';
import assert from 'node:assert/strict';
import type { RoomMessage } from '../src/types.js';
import { createTestClient, jsonResponse } from './test-helpers.js';

const ROOM_ID = '33333333-3333-3333-3333-333333333333';

test('reconnect gap-fill pages history with afterSeq and marks messages replayed', async (t) => {
  const { client, fake } = createTestClient({ gapFillPageSize: 2 });
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();

  const received: RoomMessage[] = [];
  room.onMessage((message) => received.push(message));

  // Seed lastSeq via a live message before the drop.
  fake.emit('ReceiveMessage', { roomId: ROOM_ID, seq: 10, senderId: 'alice', time: 't', content: 'before drop' });

  const fetchCalls: string[] = [];
  t.mock.method(globalThis, 'fetch', async (url: string) => {
    fetchCalls.push(url.toString());
    const parsed = new URL(url.toString());
    const afterSeq = Number(parsed.searchParams.get('afterSeq'));

    if (afterSeq === 10) {
      return jsonResponse({
        roomId: ROOM_ID,
        messages: [
          { seq: 11, time: 't', senderId: 'bob', content: 'gap-1' },
          { seq: 12, time: 't', senderId: 'bob', content: 'gap-2' }
        ],
        hasMore: true
      });
    }
    if (afterSeq === 12) {
      return jsonResponse({
        roomId: ROOM_ID,
        messages: [{ seq: 13, time: 't', senderId: 'bob', content: 'gap-3' }],
        hasMore: false
      });
    }
    throw new Error(`unexpected afterSeq ${afterSeq}`);
  });

  await fake.simulateReconnected();

  assert.equal(fetchCalls.length, 2, 'should page until hasMore is false');
  assert.ok(fetchCalls[0].includes('afterSeq=10'));
  assert.ok(fetchCalls[1].includes('afterSeq=12'));
  assert.ok(fake.invokeCalls.some((c) => c.method === 'Subscribe' && c.args[0] === ROOM_ID));

  const replayed = received.filter((m) => m.seq > 10);
  assert.deepEqual(
    replayed.map((m) => m.seq),
    [11, 12, 13]
  );
  assert.ok(replayed.every((m) => m.replayed === true));

  // Live messages arriving during/after gap-fill are still delivered normally.
  fake.emit('ReceiveMessage', { roomId: ROOM_ID, seq: 14, senderId: 'alice', time: 't', content: 'live again' });
  assert.deepEqual(
    received.map((m) => m.seq),
    [10, 11, 12, 13, 14]
  );
});

test('gapFillOnReconnect: false skips replay but still resubscribes', async (t) => {
  const { client, fake } = createTestClient({ gapFillOnReconnect: false });
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();

  fake.emit('ReceiveMessage', { roomId: ROOM_ID, seq: 1, senderId: 'alice', time: 't', content: 'hi' });

  let fetchCalled = false;
  t.mock.method(globalThis, 'fetch', async () => {
    fetchCalled = true;
    return jsonResponse({ roomId: ROOM_ID, messages: [], hasMore: false });
  });

  await fake.simulateReconnected();

  assert.equal(fetchCalled, false);
  assert.ok(fake.invokeCalls.some((c) => c.method === 'Subscribe'));
});
