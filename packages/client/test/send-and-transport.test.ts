import test from 'node:test';
import assert from 'node:assert/strict';
import { HttpTransportType } from '@microsoft/signalr';
import { createTestClient, jsonResponse } from './test-helpers.js';
import { isHubSendUnavailable, toDotwireHubError } from '../src/errors.js';

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

const ACK = { seq: 9, time: '2026-01-01T00:00:00Z' };

async function hubClient(overrides: Parameters<typeof createTestClient>[0] = {}, subscribe = true) {
  const ctx = createTestClient({ hubSend: true, ...overrides });
  await ctx.client.connect();
  if (subscribe) await ctx.client.room(ROOM_ID).subscribe();
  ctx.fake.invokeCalls.length = 0;
  return ctx;
}

const hubError = (text: string) => new Error(`An unexpected error occurred invoking 'Send' on the server. HubException: ${text}`);

test('hubSend sends over the hub when connected and subscribed', async (t) => {
  const { client, fake } = await hubClient();
  const fetchMock = t.mock.method(globalThis, 'fetch', async () => jsonResponse(ACK));
  fake.invokeImpl = () => ACK;

  const ack = await client.sendMessage(ROOM_ID, 'hi');

  assert.deepEqual(fake.invokeCalls.at(-1), { method: 'Send', args: [ROOM_ID, 'hi', null] });
  assert.equal(fetchMock.mock.callCount(), 0);
  assert.deepEqual(ack, ACK);
});

test('hubSend passes the idempotency key', async () => {
  const { client, fake } = await hubClient();
  fake.invokeImpl = () => ACK;
  await client.sendMessage(ROOM_ID, 'hi', undefined, { idempotencyKey: 'k1' });
  assert.equal(fake.invokeCalls.at(-1)?.args[2], 'k1');
});

test('hubSend falls back to REST on HubSendDisabled', async (t) => {
  const { client, fake } = await hubClient();
  const fetchMock = t.mock.method(globalThis, 'fetch', async () => jsonResponse(ACK));
  fake.invokeImpl = () => {
    throw hubError('HubSendDisabled');
  };

  assert.deepEqual(await client.sendMessage(ROOM_ID, 'hi'), ACK);
  assert.equal(fetchMock.mock.callCount(), 1);
});

test('hubSend maps Forbidden without REST fallback', async (t) => {
  const { client, fake } = await hubClient();
  const fetchMock = t.mock.method(globalThis, 'fetch', async () => jsonResponse(ACK));
  fake.invokeImpl = () => {
    throw hubError('Forbidden');
  };

  await assert.rejects(
    () => client.sendMessage(ROOM_ID, 'hi'),
    (err: unknown) => {
      const e = err as { kind: string; status: number };
      assert.equal(e.kind, 'Forbidden');
      assert.equal(e.status, 403);
      return true;
    }
  );
  assert.equal(fetchMock.mock.callCount(), 0);
});

test('hubSend maps presend_rejected with reason', async () => {
  const { client, fake } = await hubClient();
  fake.invokeImpl = () => {
    throw hubError('presend_rejected:blocked');
  };

  await assert.rejects(
    () => client.sendMessage(ROOM_ID, 'hi'),
    (err: unknown) => {
      const e = err as { kind: string; reason?: string; status: number };
      assert.equal(e.kind, 'PresendRejected');
      assert.equal(e.status, 422);
      assert.equal(e.reason, 'blocked');
      return true;
    }
  );
});

test('hubSend uses REST when not subscribed or sendUrl is set', async (t) => {
  const fetchMock = t.mock.method(globalThis, 'fetch', async () => jsonResponse(ACK));

  const a = await hubClient({}, false);
  await a.client.sendMessage(ROOM_ID, 'hi');
  const b = await hubClient({ sendUrl: 'https://host.test/send' });
  await b.client.sendMessage(ROOM_ID, 'hi');

  assert.equal(fetchMock.mock.callCount(), 2);
  assert.equal(a.fake.invokeCalls.length + b.fake.invokeCalls.length, 0);
});

test('hubSend times out with an AbortError', async () => {
  const { client, fake } = await hubClient();
  fake.invokeImpl = () => new Promise(() => undefined);

  await assert.rejects(
    () => client.sendMessage(ROOM_ID, 'hi', 20),
    (err: unknown) => err instanceof DOMException && err.name === 'AbortError'
  );
});

test('toDotwireHubError maps every hub code', () => {
  const map = (m: string) => toDotwireHubError(hubError(m));
  const pick = (m: string) => {
    const e = map(m);
    return [e.status, e.kind, e.code, e.reason];
  };
  assert.deepEqual(pick('Forbidden'), [403, 'Forbidden', 'Forbidden', undefined]);
  assert.deepEqual(pick('RateLimited'), [429, 'RateLimited', 'RateLimited', undefined]);
  assert.deepEqual(pick('invalid_content'), [400, 'BadRequest', 'invalid_content', undefined]);
  assert.deepEqual(pick('invalid_user_id'), [400, 'BadRequest', 'invalid_user_id', undefined]);
  assert.deepEqual(pick('invalid_request:bad key'), [400, 'BadRequest', 'invalid_request', 'bad key']);
  assert.deepEqual(pick('presend_rejected:blocked'), [422, 'PresendRejected', 'presend_rejected', 'blocked']);
  assert.deepEqual(pick('presend_unavailable'), [503, 'Unavailable', 'presend_unavailable', undefined]);
  assert.deepEqual(pick('stream_unavailable'), [503, 'Unavailable', 'stream_unavailable', undefined]);
  assert.deepEqual(pick('Unauthorized'), [0, 'Unknown', 'Unauthorized', undefined]);
  assert.deepEqual(pick('weird'), [0, 'Unknown', 'weird', undefined]);
});

test('toDotwireHubError uses the text after the last HubException marker', () => {
  const e = toDotwireHubError(new Error('x HubException: y HubException: presend_rejected:a:b'));
  assert.deepEqual([e.kind, e.reason], ['PresendRejected', 'a:b']);
});

test('isHubSendUnavailable only matches HubSendDisabled or a missing method', () => {
  assert.equal(isHubSendUnavailable(hubError('HubSendDisabled')), true);
  assert.equal(isHubSendUnavailable(new Error("Method 'Send' does not exist.")), true);
  assert.equal(isHubSendUnavailable(hubError('Forbidden')), false);
  assert.equal(isHubSendUnavailable(hubError('presend_rejected:blocked')), false);
});
