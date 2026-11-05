import test from 'node:test';
import assert from 'node:assert/strict';
import { HubConnectionState } from '@microsoft/signalr';
import { createTestClient } from './test-helpers.js';

test('concurrent subscribes share one connection start instead of racing HubConnection.start', async () => {
  const { client, fake } = createTestClient();
  let starts = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  fake.start = async () => {
    if (fake.state !== HubConnectionState.Disconnected) {
      throw new Error("Cannot start a HubConnection that is not in the 'Disconnected' state.");
    }
    starts += 1;
    fake.state = HubConnectionState.Connecting;
    await gate;
    fake.state = HubConnectionState.Connected;
  };

  const subscriptions = Promise.all([
    client.subscribe('11111111-1111-1111-1111-111111111111'),
    client.subscribe('22222222-2222-2222-2222-222222222222'),
    client.connect()
  ]);
  release();
  await subscriptions;

  assert.equal(starts, 1);
  assert.equal(client.getConnectionState(), 'connected');
  assert.deepEqual(
    fake.invokeCalls.map((call) => call.method),
    ['Subscribe', 'Subscribe']
  );
});

test('a failed start can be retried by the next connect', async () => {
  const { client, fake } = createTestClient();
  let attempts = 0;
  fake.start = async () => {
    attempts += 1;
    if (attempts === 1) throw new Error('offline');
    fake.state = HubConnectionState.Connected;
  };

  await assert.rejects(client.connect(), /offline/);
  await client.connect();

  assert.equal(attempts, 2);
  assert.equal(client.getConnectionState(), 'connected');
});
