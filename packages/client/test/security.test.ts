import test from 'node:test';
import assert from 'node:assert/strict';
import { NO_EXP_CACHE_MS, TokenManager } from '../src/token.js';
import { createTestClient, fakeJwt, jsonResponse } from './test-helpers.js';

const ROOM_ID = '55555555-5555-4555-8555-555555555555';

test('gap-fill stops when the server reports hasMore without making progress', async (t) => {
  const { client, fake } = createTestClient({ gapFillPageSize: 2 });
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();
  fake.emit('ReceiveMessage', { roomId: ROOM_ID, seq: 10, senderId: 'alice', time: 't', content: 'seed' });

  let calls = 0;
  t.mock.method(globalThis, 'fetch', async () => {
    calls += 1;
    if (calls > 5) throw new Error('gap-fill looped');
    return jsonResponse({ roomId: ROOM_ID, messages: [{ seq: 9, time: 't', senderId: 'bob', content: 'stale' }], hasMore: true });
  });

  await fake.simulateReconnected();

  assert.equal(calls, 1);
});

test('room ids are URL-encoded in REST paths', async (t) => {
  const { client } = createTestClient();
  const urls: string[] = [];
  t.mock.method(globalThis, 'fetch', async (url: string) => {
    urls.push(url.toString());
    return jsonResponse({ roomId: 'x', messages: [], hasMore: false });
  });

  await client.getHistory('../admin/rooms');
  await client.getParticipants('a/b').catch(() => undefined);

  assert.ok(urls.every((u) => !u.includes('/../') && !u.includes('a/b')), urls.join(', '));
  assert.ok(urls[0].includes('/rooms/..%2Fadmin%2Frooms/messages'));
});

test('sendMessage forwards an idempotency key on the direct path', async (t) => {
  const { client } = createTestClient();
  const headers: Record<string, string>[] = [];
  t.mock.method(globalThis, 'fetch', async (_url: string, init: RequestInit) => {
    headers.push(init.headers as Record<string, string>);
    return jsonResponse({ seq: 1, time: 't' });
  });

  await client.sendMessage(ROOM_ID, 'hi', undefined, { idempotencyKey: 'k-1' });

  assert.equal(headers[0]['Idempotency-Key'], 'k-1');
});

test('TokenManager rejects a token endpoint that returns no string token', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => jsonResponse({ token: 42 }));
  const manager = new TokenManager('https://dotwire.test/token', 500);

  await assert.rejects(() => manager.getToken(), /token/);
});

test('TokenManager does not cache a token without exp forever', async (t) => {
  let fetches = 0;
  t.mock.method(globalThis, 'fetch', async () => {
    fetches += 1;
    return jsonResponse({ token: fakeJwt({ sub: 'alice' }) });
  });
  const realNow = Date.now;
  const start = realNow();
  const manager = new TokenManager('https://dotwire.test/token', 500);
  try {
    await manager.getToken();
    await manager.getToken();
    assert.equal(fetches, 1);

    Date.now = () => start + NO_EXP_CACHE_MS + 1000;
    await manager.getToken();
    assert.equal(fetches, 2);
  } finally {
    Date.now = realNow;
  }
});
