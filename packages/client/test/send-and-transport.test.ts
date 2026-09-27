import test from 'node:test';
import assert from 'node:assert/strict';
import { HttpTransportType } from '@microsoft/signalr';
import { createTestClient, jsonResponse } from './test-helpers.js';

const ROOM_ID = '55555555-5555-5555-5555-555555555555';

test('sendUrl mode posts to sendUrl with credentials and no bearer header', async (t) => {
  const { client } = createTestClient({ sendUrl: 'https://host.test/send' });

  let capturedUrl: string | undefined;
  let capturedInit: RequestInit | undefined;
  t.mock.method(globalThis, 'fetch', async (url: string, init: RequestInit) => {
    capturedUrl = url.toString();
    capturedInit = init;
    return jsonResponse({ seq: 42, time: '2026-01-01T00:00:00Z' });
  });

  const ack = await client.sendMessage(ROOM_ID, 'hello');

  assert.equal(capturedUrl, 'https://host.test/send');
  assert.equal(capturedInit?.method, 'POST');
  assert.equal(capturedInit?.credentials, 'include');
  assert.deepEqual(JSON.parse(capturedInit!.body as string), { roomId: ROOM_ID, content: 'hello' });
  const headers = new Headers(capturedInit?.headers);
  assert.equal(headers.has('Authorization'), false);
  assert.deepEqual(ack, { seq: 42, time: '2026-01-01T00:00:00Z' });
});

test('default send mode posts to {baseUrl}/rooms/{id}/messages with a bearer token', async (t) => {
  const { client } = createTestClient();

  let capturedUrl: string | undefined;
  let capturedInit: RequestInit | undefined;
  t.mock.method(globalThis, 'fetch', async (url: string, init: RequestInit) => {
    capturedUrl = url.toString();
    capturedInit = init;
    return jsonResponse({ seq: 1, time: '2026-01-01T00:00:00Z' });
  });

  await client.sendMessage(ROOM_ID, 'hi');

  assert.equal(capturedUrl, `https://dotwire.test/rooms/${ROOM_ID}/messages`);
  const headers = new Headers(capturedInit?.headers);
  assert.equal(headers.get('Authorization'), 'Bearer test-token');
  assert.equal(capturedInit?.credentials, undefined);
});

test('presend rejection (422) maps to DotwireClientError with kind PresendRejected', async (t) => {
  const { client } = createTestClient();

  t.mock.method(globalThis, 'fetch', async () =>
    new Response(JSON.stringify({ error: 'presend_rejected', reason: 'blocked' }), { status: 422 })
  );

  await assert.rejects(
    () => client.sendMessage(ROOM_ID, 'bad'),
    (err: unknown) => {
      const e = err as { kind: string; code?: string; reason?: string };
      assert.equal(e.kind, 'PresendRejected');
      assert.equal(e.code, 'presend_rejected');
      assert.equal(e.reason, 'blocked');
      return true;
    }
  );
});

test('rate limiting (429) maps to DotwireClientError with retryAfterMs', async (t) => {
  const { client } = createTestClient();

  t.mock.method(globalThis, 'fetch', async () =>
    new Response(JSON.stringify({ error: 'rate_limited' }), {
      status: 429,
      headers: { 'Retry-After': '5' }
    })
  );

  await assert.rejects(
    () => client.sendMessage(ROOM_ID, 'x'),
    (err: unknown) => {
      const e = err as { kind: string; retryAfterMs?: number };
      assert.equal(e.kind, 'RateLimited');
      assert.equal(e.retryAfterMs, 5000);
      return true;
    }
  );
});

test('default transport requests websockets with skipNegotiation', () => {
  const { lastFactoryOptions } = createTestClient();
  assert.equal(lastFactoryOptions?.transport, HttpTransportType.WebSockets);
  assert.equal(lastFactoryOptions?.skipNegotiation, true);
});

test('transport: "negotiate" leaves transport/skipNegotiation unset', () => {
  const { lastFactoryOptions } = createTestClient({ transport: 'negotiate' });
  assert.equal(lastFactoryOptions?.transport, undefined);
  assert.equal(lastFactoryOptions?.skipNegotiation, undefined);
});

test('connectionFactory receives the configured hub URL, honouring hubPath', () => {
  const { lastFactoryUrl } = createTestClient({ hubPath: '/custom/hub' });
  assert.equal(lastFactoryUrl, 'https://dotwire.test/custom/hub');
});
