import test from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { DotwireHostClient, roomPath } from '../src/client.js';
import { DotwireTokenSigner } from '../src/signer.js';
import { ModerationQueue } from '../src/moderation.js';
import { MAX_SSE_BUFFER_CHARS, parseSseStream, toRoomEvent } from '../src/sse.js';
import { baseConfig, createFakeFetch, jsonRes } from './helpers.js';

const ROOM = '44444444-4444-4444-8444-444444444444';
const SECRET = 'shh';

function presendBody(): string {
  return JSON.stringify({ roomId: ROOM, senderId: 'alice', content: 'hi', time: 't', source: 'member' });
}

function sign(payload: string): string {
  return `sha256=${createHmac('sha256', SECRET).update(payload).digest('hex')}`;
}

function signedRequest(body: string, timestampSeconds: number, extra: Record<string, string> = {}): Request {
  const ts = String(timestampSeconds);
  return new Request('https://host.test/webhooks/presend', {
    method: 'POST',
    headers: {
      'X-Dotwire-Signature': sign(body),
      'X-Dotwire-Timestamp': ts,
      'X-Dotwire-Signature-V2': sign(`${ts}.${body}`),
      ...extra
    },
    body
  });
}

function chunked(parts: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const part of parts) controller.enqueue(encoder.encode(part));
      controller.close();
    }
  });
}

async function collect(stream: ReadableStream<Uint8Array>) {
  const events = [];
  for await (const evt of parseSseStream(stream)) events.push(evt);
  return events;
}

test('webhook handlers refuse to run without a secret unless unsigned calls are explicitly allowed', async () => {
  const client = new DotwireHostClient(baseConfig());

  await assert.rejects(() => client.handlePresendWebhook(new Request('https://h.test', { method: 'POST', body: presendBody() })), /webhookSecret/);
  await assert.rejects(() => client.handlePostSendWebhookBody({}, null), /webhookSecret/);
  await assert.rejects(() => client.handlePresendWebhookBody({}, null), /webhookSecret/);
});

test('timestamped webhook signatures are accepted', async () => {
  const client = new DotwireHostClient(baseConfig({ webhookSecret: SECRET }));

  const res = await client.handlePresendWebhook(signedRequest(presendBody(), Math.floor(Date.now() / 1000)));

  assert.equal(res.status, 200);
});

test('replayed webhook calls with a stale timestamp are rejected', async () => {
  const client = new DotwireHostClient(baseConfig({ webhookSecret: SECRET }));

  const res = await client.handlePresendWebhook(signedRequest(presendBody(), Math.floor(Date.now() / 1000) - 600));

  assert.equal(res.status, 401);
});

test('a forged timestamp does not match the timestamped signature', async () => {
  const client = new DotwireHostClient(baseConfig({ webhookSecret: SECRET }));
  const old = Math.floor(Date.now() / 1000) - 600;
  const request = signedRequest(presendBody(), old, { 'X-Dotwire-Timestamp': String(Math.floor(Date.now() / 1000)) });

  const res = await client.handlePresendWebhook(request);

  assert.equal(res.status, 401);
});

test('legacy body-only signatures need an explicit opt-in', async () => {
  const body = presendBody();
  const legacyOnly = () =>
    new Request('https://h.test', { method: 'POST', headers: { 'X-Dotwire-Signature': sign(body) }, body });
  const strict = new DotwireHostClient(baseConfig({ webhookSecret: SECRET }));
  const legacy = new DotwireHostClient(baseConfig({ webhookSecret: SECRET, allowLegacyWebhookSignatures: true }));

  assert.equal((await strict.handlePresendWebhook(legacyOnly())).status, 401);
  assert.equal((await legacy.handlePresendWebhook(legacyOnly())).status, 200);
});

test('the Express-style body handler verifies timestamped signatures passed explicitly', async () => {
  const client = new DotwireHostClient(baseConfig({ webhookSecret: SECRET }));
  const raw = presendBody();
  const ts = String(Math.floor(Date.now() / 1000));

  const result = await client.handlePresendWebhookBody(JSON.parse(raw), null, raw, { timestamp: ts, signatureV2: sign(`${ts}.${raw}`) });

  assert.equal(result.status, 200);
});

test('oversized webhook bodies are rejected with 413 before signature checks', async () => {
  const client = new DotwireHostClient(baseConfig({ webhookSecret: SECRET, maxWebhookBodyBytes: 32 }));
  const declared = new Request('https://h.test', { method: 'POST', headers: { 'content-length': '999999' }, body: 'x' });
  const streamed = new Request('https://h.test', { method: 'POST', body: chunked(['x'.repeat(20), 'y'.repeat(20)]), duplex: 'half' } as RequestInit);

  assert.equal((await client.handlePresendWebhook(declared)).status, 413);
  assert.equal((await client.handlePostSendWebhook(streamed)).status, 413);
});

test('oversized client sends are rejected with 413 and never forwarded', async () => {
  const { fetchImpl, calls } = createFakeFetch(() => jsonRes(202, { seq: 1, time: 't' }));
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl, maxWebhookBodyBytes: 16 }));

  const res = await client.handleClientSend(new Request('https://h.test', { method: 'POST', body: JSON.stringify({ roomId: ROOM, content: 'x'.repeat(64) }) }), 'dave');

  assert.equal(res.status, 413);
  assert.equal(calls.length, 0);
});

test('room ids that are not UUIDs never reach a URL', async () => {
  const { fetchImpl, calls } = createFakeFetch(() => jsonRes(202, { seq: 1, time: 't' }));
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const traversal = `${ROOM}/../../admin/rooms/${ROOM}/messages`;

  const result = await client.handleClientSendBody({ roomId: traversal, content: 'hi' }, 'admin-user');
  await assert.rejects(() => client.sendAsUser(traversal, 'dave', 'hi'), TypeError);
  await assert.rejects(() => client.redactMessage(traversal, 1), TypeError);
  await assert.rejects(() => client.getRoomMembers('room1'), TypeError);

  assert.equal(result.status, 400);
  assert.equal(calls.length, 0);
  assert.equal(roomPath(ROOM.toUpperCase()), ROOM.toUpperCase());
});

test('redactMessage rejects non-integer sequence numbers', async () => {
  const { fetchImpl, calls } = createFakeFetch(() => jsonRes(200, {}));
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));

  for (const seq of [0, -1, 1.5, Number.NaN, 2 ** 60]) {
    await assert.rejects(() => client.redactMessage(ROOM, seq), TypeError);
  }
  assert.equal(calls.length, 0);
});

test('presend hook failures return a generic reason instead of the internal error message', async () => {
  const client = new DotwireHostClient(
    baseConfig({
      allowUnsignedWebhooks: true,
      presend: () => {
        throw new Error('db password is hunter2');
      }
    })
  );

  const res = await client.handlePresendWebhook(new Request('https://h.test', { method: 'POST', body: presendBody() }));

  assert.deepEqual(await res.json(), { allow: false, reason: 'presend hook failed' });
});

test('sendAsUser and handleClientSendBody forward an idempotency key', async () => {
  const { fetchImpl, calls } = createFakeFetch(() => jsonRes(202, { seq: 1, time: 't' }));
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));

  await client.sendAsUser(ROOM, 'dave', 'hi', { idempotencyKey: 'k-1' });
  await client.handleClientSendBody({ roomId: ROOM, content: 'hi', idempotencyKey: 'k-2' }, 'dave');

  assert.deepEqual(calls.map((c) => c.headers['idempotency-key']), ['k-1', 'k-2']);
});

test('the signer rejects unknown roles and a bad key never becomes an unhandled rejection', async () => {
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown) => unhandled.push(reason);
  process.on('unhandledRejection', onUnhandled);
  try {
    const broken = new DotwireTokenSigner(baseConfig({ privateKeyPem: 'not a key' }));
    await new Promise((resolve) => setTimeout(resolve, 20));
    await assert.rejects(() => broken.mintToken('u1'));

    const signer = new DotwireTokenSigner(baseConfig());
    await assert.rejects(() => signer.mintToken('u1', { role: 'root' as never }), TypeError);
  } finally {
    process.off('unhandledRejection', onUnhandled);
  }
  assert.deepEqual(unhandled, []);
});

test('SSE parsing handles CRLF split across chunks, lone CR and keeps significant spaces', async () => {
  const events = await collect(chunked(['event: message\r', '\ndata:  two spaces\r\n\r', '\nevent: ping\rdata: x\r\r']));

  assert.deepEqual(events, [
    { event: 'message', data: ' two spaces', id: undefined },
    { event: 'ping', data: 'x', id: undefined }
  ]);
});

test('SSE parsing gives up on an event that never terminates', async () => {
  await assert.rejects(() => collect(chunked(['data: ', 'x'.repeat(MAX_SSE_BUFFER_CHARS + 10)])), /maximum buffered size/);
});

test('toRoomEvent drops events with invalid numbers or missing fields', () => {
  const message = (data: object) => toRoomEvent({ event: 'message', data: JSON.stringify(data) });

  assert.equal(message({ roomId: ROOM, seq: 'NaN', senderId: 'a', time: 't', content: 'c' }), null);
  assert.equal(message({ roomId: ROOM, seq: 1.5, senderId: 'a', time: 't', content: 'c' }), null);
  assert.equal(message({ roomId: ROOM, seq: 1, time: 't', content: 'c' }), null);
  assert.equal(toRoomEvent({ event: 'retracted', data: JSON.stringify({ roomId: ROOM }) }), null);
  assert.equal(toRoomEvent({ event: 'revoked', data: '{}' }), null);
  assert.deepEqual(message({ roomId: ROOM, seq: 3, senderId: 'a', time: 't', content: 'c' }), {
    type: 'message',
    roomId: ROOM,
    seq: 3,
    senderId: 'a',
    time: 't',
    content: 'c',
    replayed: false
  });
});

test('a full moderation queue rejects aborted and timed-out enqueues instead of hanging', async () => {
  const queue = new ModerationQueue(1, { enqueueTimeoutMs: 30 });
  const item = { roomId: ROOM, seq: 1, time: 't', senderUserId: 'u', content: 'c' };
  await queue.enqueue(item);

  const controller = new AbortController();
  controller.abort(new Error('caller gave up'));
  await assert.rejects(() => queue.enqueue(item, controller.signal), /caller gave up/);
  await assert.rejects(() => queue.enqueue(item), /stayed full/);
});
