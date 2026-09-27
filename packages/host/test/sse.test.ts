import test from 'node:test';
import assert from 'node:assert/strict';
import { DotwireHostClient } from '../src/client.js';
import { parseSseStream, toRoomEvent } from '../src/sse.js';
import { baseConfig, createFakeFetch, type RecordedCall } from './helpers.js';
import type { RoomEvent } from '../src/types.js';

function streamOf(text: string): ReadableStream<Uint8Array> {
  const bytes = new TextEncoder().encode(text);
  return new ReadableStream({
    start(controller) {
      controller.enqueue(bytes);
      controller.close();
    }
  });
}

test('parseSseStream + toRoomEvent handle every event type and ignore ping comments', async () => {
  const text =
    'id: 1\nevent: message\ndata: {"roomId":"r1","seq":1,"senderId":"a","time":"t","content":"hi","replayed":true}\n\n' +
    ': ping\n\n' +
    'event: retracted\ndata: {"roomId":"r1","seq":1}\n\n' +
    'event: typing\ndata: {"roomId":"r1","userId":"a","isTyping":true}\n\n' +
    'event: presence\ndata: {"roomId":"r1","joined":["a"],"left":[]}\n\n' +
    'event: revoked\ndata: {"roomId":"r1"}\n\n';

  const events: RoomEvent[] = [];
  for await (const raw of parseSseStream(streamOf(text))) {
    const evt = toRoomEvent(raw);
    if (evt) events.push(evt);
  }

  assert.deepEqual(events, [
    { type: 'message', roomId: 'r1', seq: 1, senderId: 'a', time: 't', content: 'hi', replayed: true },
    { type: 'retracted', roomId: 'r1', seq: 1 },
    { type: 'typing', roomId: 'r1', userId: 'a', isTyping: true },
    { type: 'presence', roomId: 'r1', joined: ['a'], left: [] },
    { type: 'revoked', roomId: 'r1' }
  ]);
});

test('streamRoomEvents yields typed events and stops after revoked', async () => {
  const text = 'id: 1\nevent: message\ndata: {"roomId":"r1","seq":1,"senderId":"a","time":"t","content":"hi"}\n\n' + 'event: revoked\ndata: {"roomId":"r1"}\n\n';
  const { fetchImpl } = createFakeFetch(
    () => new Response(streamOf(text), { status: 200, headers: { 'content-type': 'text/event-stream' } })
  );
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));

  const events: RoomEvent[] = [];
  for await (const evt of client.streamRoomEvents('r1', { asUserId: 'dave' })) {
    events.push(evt);
  }
  assert.equal(events.length, 2);
  assert.equal(events[0].type, 'message');
  assert.equal(events[1].type, 'revoked');
});

test('streamRoomEvents reconnects with Last-Event-ID set to the last seen seq after a mid-stream drop', async () => {
  const calls: RecordedCall[] = [];
  let attempt = 0;
  const { fetchImpl } = createFakeFetch((call) => {
    calls.push(call);
    attempt++;
    if (attempt === 1) {
      // First connection: yield one message then the stream ends abruptly (simulated drop).
      return new Response(streamOf('id: 5\nevent: message\ndata: {"roomId":"r1","seq":5,"senderId":"a","time":"t","content":"hi"}\n\n'), {
        status: 200
      });
    }
    // Second connection (the resume): assert Last-Event-ID was sent, then close with revoked to end the test.
    assert.equal(call.headers['last-event-id'], '5');
    return new Response(streamOf('event: revoked\ndata: {"roomId":"r1"}\n\n'), { status: 200 });
  });

  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const events: RoomEvent[] = [];
  for await (const evt of client.streamRoomEvents('r1', { asUserId: 'dave', autoResume: true })) {
    events.push(evt);
  }

  assert.equal(attempt, 2);
  assert.equal(events.length, 2);
  assert.equal(events[0].type, 'message');
  assert.equal(events[1].type, 'revoked');
});

test('streamRoomEvents does not reconnect when autoResume is false', async () => {
  let attempt = 0;
  const { fetchImpl } = createFakeFetch(() => {
    attempt++;
    return new Response(streamOf('id: 1\nevent: message\ndata: {"roomId":"r1","seq":1,"senderId":"a","time":"t","content":"hi"}\n\n'), {
      status: 200
    });
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const events: RoomEvent[] = [];
  for await (const evt of client.streamRoomEvents('r1', { asUserId: 'dave', autoResume: false })) {
    events.push(evt);
  }
  assert.equal(attempt, 1);
  assert.equal(events.length, 1);
});
