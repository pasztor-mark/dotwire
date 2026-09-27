import test from 'node:test';
import assert from 'node:assert/strict';
import { createTestClient } from './test-helpers.js';

const ROOM_ID = '44444444-4444-4444-4444-444444444444';
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test('notifyTyping respects typingThrottleMs', async () => {
  const { client, fake } = createTestClient({ typingThrottleMs: 60, autoStopTypingMs: 10_000 });
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();
  fake.invokeCalls.length = 0;

  await room.notifyTyping();
  await room.notifyTyping();
  await room.notifyTyping();

  const typingInvokes = () => fake.invokeCalls.filter((c) => c.method === 'Typing' && c.args[1] === true);
  assert.equal(typingInvokes().length, 1, 'calls within the throttle window collapse to one send');

  await wait(80);
  await room.notifyTyping();
  assert.equal(typingInvokes().length, 2, 'a call after the throttle window sends again');
});

test('stopTyping sends isTyping=false and clears state', async () => {
  const { client, fake } = createTestClient({ typingThrottleMs: 1000, autoStopTypingMs: 10_000 });
  await client.connect();
  const room = client.room(ROOM_ID);
  await room.subscribe();
  fake.invokeCalls.length = 0;

  await room.startTyping();
  await room.stopTyping();

  const calls = fake.invokeCalls.filter((c) => c.method === 'Typing');
  assert.deepEqual(
    calls.map((c) => c.args[1]),
    [true, false]
  );
});
