import test from 'node:test';
import assert from 'node:assert/strict';
import { decodeJwt, importSPKI, jwtVerify } from 'jose';
import { DotwireHostClient } from '../src/client.js';
import { DotwireApiError, PostSendError, PresendRejectedError } from '../src/errors.js';
import { baseConfig, createFakeFetch, createRoutedFetch, jsonRes, type RecordedCall } from './helpers.js';
import { TEST_PUBLIC_KEY_PEM } from './keys.js';

function bearerToken(call: RecordedCall): string {
  const auth = call.headers.authorization ?? call.headers.Authorization;
  assert.ok(auth, 'expected an Authorization header');
  return auth!.replace(/^Bearer /, '');
}

// ---------------------------------------------------------------------
// mintToken / tokenResponse
// ---------------------------------------------------------------------

test('mintToken produces a JWT verifiable against the public key, with the right claims', async () => {
  const client = new DotwireHostClient(baseConfig());
  const token = await client.mintToken('alice', { role: 'auditor', displayName: 'Alice' });

  const publicKey = await importSPKI(TEST_PUBLIC_KEY_PEM, 'RS256');
  const { payload, protectedHeader } = await jwtVerify(token, publicKey, {
    issuer: 'https://issuer.test',
    audience: 'dotwire'
  });

  assert.equal(protectedHeader.alg, 'RS256');
  assert.equal(protectedHeader.kid, 'test-key');
  assert.equal(payload.sub, 'alice');
  assert.equal((payload as Record<string, unknown>)['dw:role'], 'auditor');
  assert.equal((payload as Record<string, unknown>)['dw:name'], 'Alice');
  assert.ok(typeof payload.exp === 'number');
});

test('tokenResponse returns a Response with { token } and default role member when unspecified role lookup 404s', async () => {
  const { fetchImpl, calls } = createRoutedFetch({
    'GET /admin/users/newbie/role': () => jsonRes(404, {})
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));

  const res = await client.tokenResponse({ userId: 'newbie' });
  assert.equal(res.status, 200);
  assert.equal(res.headers.get('content-type'), 'application/json');
  const body = (await res.json()) as { token: string };
  const payload = decodeJwt(body.token);
  assert.equal(payload.sub, 'newbie');
  assert.equal((payload as Record<string, unknown>)['dw:role'], 'member');
  assert.equal(calls.length, 1);

  // Second call should hit the role cache, not the admin API again.
  const res2 = await client.tokenResponse({ userId: 'newbie' });
  const body2 = (await res2.json()) as { token: string };
  assert.equal((decodeJwt(body2.token) as Record<string, unknown>)['dw:role'], 'member');
  assert.equal(calls.length, 1, 'role lookup should be cached');
});

test('tokenResponse with an explicit role bypasses lookup entirely', async () => {
  const { fetchImpl, calls } = createFakeFetch(() => {
    throw new Error('should not call the network');
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const res = await client.tokenResponse({ userId: 'bob', role: 'admin' });
  const body = (await res.json()) as { token: string };
  assert.equal((decodeJwt(body.token) as Record<string, unknown>)['dw:role'], 'admin');
  assert.equal(calls.length, 0);
});

// ---------------------------------------------------------------------
// Roles
// ---------------------------------------------------------------------

test('setUserRole PUTs as admin and returns the response body', async () => {
  const { fetchImpl, calls } = createRoutedFetch({
    'PUT /admin/users/carol/role': (call) => {
      assert.deepEqual(JSON.parse(call.body!), { role: 'auditor' });
      return jsonRes(200, { userId: 'carol', role: 'auditor' });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const result = await client.setUserRole('carol', 'auditor');
  assert.deepEqual(result, { userId: 'carol', role: 'auditor' });

  const payload = decodeJwt(bearerToken(calls[0]));
  assert.equal(payload.sub, 'host-admin');
  assert.equal((payload as Record<string, unknown>)['dw:role'], 'admin');
});

test('getUserRole returns null on 404', async () => {
  const { fetchImpl } = createRoutedFetch({ 'GET /admin/users/x/role': () => jsonRes(404, {}) });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  assert.equal(await client.getUserRole('x'), null);
});

test('deleteUserRole swallows 404', async () => {
  const { fetchImpl } = createRoutedFetch({ 'DELETE /admin/users/x/role': () => new Response(null, { status: 404 }) });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  await assert.doesNotReject(() => client.deleteUserRole('x'));
});

test('getUserRooms returns roomIds', async () => {
  const { fetchImpl } = createRoutedFetch({
    'GET /admin/users/x/rooms': () => jsonRes(200, { userId: 'x', roomIds: ['r1', 'r2'] })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  assert.deepEqual(await client.getUserRooms('x'), ['r1', 'r2']);
});

// ---------------------------------------------------------------------
// Rooms / membership
// ---------------------------------------------------------------------

test('addRoomMembers defaults ensureMemberRole to true and posts the ids', async () => {
  const { fetchImpl } = createRoutedFetch({
    'POST /admin/rooms/room1/members': (call) => {
      assert.deepEqual(JSON.parse(call.body!), { userIds: ['a', 'b'], ensureMemberRole: true });
      return jsonRes(200, { roomId: 'room1', members: ['a', 'b'] });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  assert.deepEqual(await client.addRoomMembers('room1', ['a', 'b']), ['a', 'b']);
});

test('addRoomMembers honours ensureMemberRole: false', async () => {
  const { fetchImpl } = createRoutedFetch({
    'POST /admin/rooms/room1/members': (call) => {
      assert.deepEqual(JSON.parse(call.body!), { userIds: ['a'], ensureMemberRole: false });
      return jsonRes(200, { roomId: 'room1', members: ['a'] });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  await client.addRoomMembers('room1', ['a'], { ensureMemberRole: false });
});

test('removeRoomMember swallows 404, getRoomMembers returns members', async () => {
  const { fetchImpl } = createRoutedFetch({
    'DELETE /admin/rooms/room1/members/a': () => new Response(null, { status: 404 }),
    'GET /admin/rooms/room1/members': () => jsonRes(200, { roomId: 'room1', members: ['a', 'b'] })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  await assert.doesNotReject(() => client.removeRoomMember('room1', 'a'));
  assert.deepEqual(await client.getRoomMembers('room1'), ['a', 'b']);
});

test('getRoomParticipants reads as the requested room member', async () => {
  const { fetchImpl, calls } = createRoutedFetch({
    'GET /rooms/room1/participants': () => jsonRes(200, ['alice', 'bob'])
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  assert.deepEqual(await client.getRoomParticipants('room1', 'alice'), ['alice', 'bob']);
  const payload = decodeJwt(bearerToken(calls[0]));
  assert.equal(payload.sub, 'alice');
  assert.equal((payload as Record<string, unknown>)['dw:role'], 'member');
});

// ---------------------------------------------------------------------
// Sending, role resolution
// ---------------------------------------------------------------------

test('sendAsUser tries member, resolves role on 403, retries once, and succeeds', async () => {
  let sendCount = 0;
  const { fetchImpl, calls } = createFakeFetch((call) => {
    const url = new URL(call.url);
    if (call.method === 'POST' && url.pathname === '/rooms/room1/messages') {
      sendCount++;
      const role = (decodeJwt(bearerToken(call)) as Record<string, unknown>)['dw:role'];
      if (role === 'member') return jsonRes(403, {});
      return jsonRes(202, { seq: 5, time: '2026-09-07T00:00:00Z' });
    }
    if (call.method === 'GET' && url.pathname === '/admin/users/dave/role') {
      return jsonRes(200, { userId: 'dave', role: 'admin' });
    }
    throw new Error(`unexpected call: ${call.method} ${url.pathname}`);
  });

  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const ack = await client.sendAsUser('room1', 'dave', 'hi');
  assert.deepEqual(ack, { seq: 5, time: '2026-09-07T00:00:00Z' });
  assert.equal(sendCount, 2);

  const roleCalls = calls.filter((c) => new URL(c.url).pathname === '/admin/users/dave/role');
  assert.equal(roleCalls.length, 1);
});

test('a second 403 after role resolution surfaces as a Forbidden DotwireApiError', async () => {
  const { fetchImpl } = createFakeFetch((call) => {
    const url = new URL(call.url);
    if (call.method === 'POST' && url.pathname === '/rooms/room1/messages') return jsonRes(403, {});
    if (call.method === 'GET' && url.pathname === '/admin/users/eve/role') return jsonRes(200, { userId: 'eve', role: 'member' });
    throw new Error('unexpected');
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  await assert.rejects(
    () => client.sendAsUser('room1', 'eve', 'hi'),
    (err: unknown) => err instanceof DotwireApiError && err.kind === 'Forbidden' && err.status === 403
  );
});

test('local presend hook can reject before any network call', async () => {
  const { fetchImpl, calls } = createFakeFetch(() => {
    throw new Error('should not reach the network');
  });
  const client = new DotwireHostClient(
    baseConfig({ fetch: fetchImpl, presend: () => ({ allow: false, rejectionReason: 'blocked word' }) })
  );
  await assert.rejects(
    () => client.sendAsUser('room1', 'dave', 'bad word'),
    (err: unknown) => err instanceof PresendRejectedError && err.reason === 'blocked word'
  );
  assert.equal(calls.length, 0);
});

test('local presend hook can rewrite content', async () => {
  let sentContent: string | undefined;
  const { fetchImpl } = createFakeFetch((call) => {
    sentContent = (JSON.parse(call.body!) as { content: string }).content;
    return jsonRes(202, { seq: 1, time: 't' });
  });
  const client = new DotwireHostClient(
    baseConfig({ fetch: fetchImpl, presend: (ctx) => ({ allow: true, content: `${ctx.content} [redacted]` }) })
  );
  await client.sendAsUser('room1', 'dave', 'raw content');
  assert.equal(sentContent, 'raw content [redacted]');
});

test('postSend hook failure throws PostSendError carrying the ack; the send already happened', async () => {
  const { fetchImpl } = createFakeFetch(() => jsonRes(202, { seq: 9, time: 't' }));
  const client = new DotwireHostClient(
    baseConfig({
      fetch: fetchImpl,
      postSend: () => {
        throw new Error('moderation queue full');
      }
    })
  );
  await assert.rejects(
    () => client.sendAsUser('room1', 'dave', 'hi'),
    (err: unknown) => err instanceof PostSendError && err.sent.seq === 9
  );
});

test('sendSystemMessage uses admin injection and does not run presend/postSend hooks', async () => {
  let presendCalls = 0;
  let postSendCalls = 0;
  const { fetchImpl, calls } = createRoutedFetch({
    'POST /admin/rooms/room1/messages': (call) => {
      assert.deepEqual(JSON.parse(call.body!), { content: 'hello', senderId: 'system' });
      return jsonRes(202, { seq: 1, time: 't' });
    }
  });
  const client = new DotwireHostClient(
    baseConfig({
      fetch: fetchImpl,
      presend: () => {
        presendCalls++;
        return { allow: true };
      },
      postSend: () => {
        postSendCalls++;
      }
    })
  );
  const ack = await client.sendSystemMessage('room1', 'hello');
  assert.deepEqual(ack, { seq: 1, time: 't' });
  assert.equal(presendCalls, 0);
  assert.equal(postSendCalls, 0);

  const payload = decodeJwt(bearerToken(calls[0]));
  assert.equal(payload.sub, 'host-admin');
});

test('sendSystemMessage honours a custom senderId', async () => {
  const { fetchImpl } = createRoutedFetch({
    'POST /admin/rooms/room1/messages': (call) => {
      assert.deepEqual(JSON.parse(call.body!), { content: 'hi', senderId: 'bot:1' });
      return jsonRes(202, { seq: 1, time: 't' });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  await client.sendSystemMessage('room1', 'hi', { senderId: 'bot:1' });
});

// ---------------------------------------------------------------------
// Messages, retention, audit, redaction: verb/path/query wiring
// ---------------------------------------------------------------------

test('getMessages sends afterSeq/beforeSeq/limit as query params, resolving role for the given user', async () => {
  const { fetchImpl } = createRoutedFetch({
    'GET /rooms/room1/messages': (call) => {
      const url = new URL(call.url);
      assert.equal(url.searchParams.get('afterSeq'), '10');
      assert.equal(url.searchParams.get('limit'), '20');
      return jsonRes(200, { roomId: 'room1', messages: [], hasMore: false });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const page = await client.getMessages('room1', { asUserId: 'dave', afterSeq: 10, limit: 20 });
  assert.equal(page.hasMore, false);
});

test('redactMessage DELETEs as admin', async () => {
  const { fetchImpl, calls } = createRoutedFetch({
    'DELETE /admin/rooms/room1/messages/7': () =>
      jsonRes(200, { roomId: 'room1', seq: 7, removedFromHistory: true, removedFromStream: true })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const result = await client.redactMessage('room1', 7);
  assert.equal(result.removedFromHistory, true);
  assert.equal((decodeJwt(bearerToken(calls[0])) as Record<string, unknown>)['dw:role'], 'admin');
});

test('retention get/set', async () => {
  const { fetchImpl } = createRoutedFetch({
    'GET /admin/retention': () => jsonRes(200, { messagesDays: 30 }),
    'PUT /admin/retention': (call) => {
      assert.deepEqual(JSON.parse(call.body!), { messagesDays: 90 });
      return jsonRes(200, { messagesDays: 90 });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  assert.equal(await client.getMessageRetentionDays(), 30);
  assert.equal(await client.setMessageRetentionDays(90), 90);
});

test('audit endpoints mint as the auditor and pass through query params', async () => {
  const { fetchImpl, calls } = createRoutedFetch({
    'GET /audit': (call) => {
      const url = new URL(call.url);
      assert.equal(url.searchParams.get('afterId'), '5');
      assert.equal(url.searchParams.get('limit'), '50');
      return jsonRes(200, { events: [], hasMore: false });
    },
    'GET /audit/verify': (call) => {
      assert.equal(new URL(call.url).searchParams.get('full'), 'true');
      return jsonRes(200, { ok: true, fromId: 0, toId: 10, checked: 10, firstInvalidId: null, anchor: null });
    },
    'GET /audit/checkpoints': () => jsonRes(200, { checkpoints: [{ day: '2026-09-01', lastId: 1, hash: 'ab', createdAt: 't' }] })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  await client.readAuditLog(5, 50);
  const verification = await client.verifyAuditLog(true);
  assert.equal(verification.ok, true);
  const checkpoints = await client.getAuditCheckpoints();
  assert.equal(checkpoints.length, 1);

  for (const call of calls) {
    const payload = decodeJwt(bearerToken(call));
    assert.equal(payload.sub, 'host-auditor');
    assert.equal((payload as Record<string, unknown>)['dw:role'], 'auditor');
  }
});

test('exportUser and exportUserStream', async () => {
  const exportBody = { userId: 'dave', exportedAt: 't', role: 'member', roomIds: [], messages: [], auditEvents: [] };
  const { fetchImpl } = createRoutedFetch({
    'GET /admin/users/dave/export': () => jsonRes(200, exportBody)
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  assert.deepEqual(await client.exportUser('dave'), exportBody);

  const { fetchImpl: streamFetch } = createRoutedFetch({
    'GET /admin/users/dave/export': () => jsonRes(200, exportBody)
  });
  const client2 = new DotwireHostClient(baseConfig({ fetch: streamFetch }));
  const stream = await client2.exportUserStream('dave');
  const text = await new Response(stream).text();
  assert.deepEqual(JSON.parse(text), exportBody);
});

// ---------------------------------------------------------------------
// Error mapping
// ---------------------------------------------------------------------

test('error mapping: status/code -> DotwireApiError.kind', async () => {
  const cases: Array<{ status: number; body: unknown; headers?: Record<string, string>; kind: string }> = [
    { status: 400, body: { error: 'invalid_content' }, kind: 'BadRequest' },
    { status: 401, body: {}, kind: 'Unauthorized' },
    { status: 403, body: {}, kind: 'Forbidden' },
    { status: 404, body: {}, kind: 'NotFound' },
    { status: 422, body: { error: 'presend_rejected', reason: 'nope' }, kind: 'PresendRejected' },
    { status: 429, body: { error: 'rate_limited' }, headers: { 'retry-after': '2' }, kind: 'RateLimited' },
    { status: 503, body: { error: 'audit_unavailable' }, kind: 'Unavailable' },
    { status: 418, body: {}, kind: 'Unknown' }
  ];

  for (const c of cases) {
    const { fetchImpl } = createRoutedFetch({
      'GET /admin/rooms/room1/members': () => jsonRes(c.status, c.body, c.headers)
    });
    const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
    await assert.rejects(
      () => client.getRoomMembers('room1'),
      (err: unknown) => {
        if (!(err instanceof DotwireApiError)) throw new Error(`expected DotwireApiError for status ${c.status}`);
        assert.equal(err.status, c.status);
        assert.equal(err.kind, c.kind);
        if (c.status === 429) assert.equal(err.retryAfterMs, 2000);
        return true;
      }
    );
  }
});

// ---------------------------------------------------------------------
// Request handlers
// ---------------------------------------------------------------------

test('handleClientSend parses the request, sends as the given user, and mirrors the ack', async () => {
  const { fetchImpl } = createRoutedFetch({
    'POST /rooms/room1/messages': () => jsonRes(202, { seq: 3, time: 't' })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const request = new Request('https://host.test/api/chat/send', {
    method: 'POST',
    body: JSON.stringify({ roomId: 'room1', content: 'hi' })
  });
  const res = await client.handleClientSend(request, 'dave');
  assert.equal(res.status, 202);
  assert.deepEqual(await res.json(), { seq: 3, time: 't' });
});

test('handleClientSend maps a presend rejection to 422', async () => {
  const client = new DotwireHostClient(
    baseConfig({ presend: () => ({ allow: false, rejectionReason: 'nope' }) })
  );
  const request = new Request('https://host.test/api/chat/send', {
    method: 'POST',
    body: JSON.stringify({ roomId: 'room1', content: 'hi' })
  });
  const res = await client.handleClientSend(request, 'dave');
  assert.equal(res.status, 422);
  assert.deepEqual(await res.json(), { error: 'presend_rejected', reason: 'nope' });
});

test('handleClientSend maps a server DotwireApiError to its status/code', async () => {
  const { fetchImpl } = createRoutedFetch({
    'POST /rooms/room1/messages': () => jsonRes(429, { error: 'rate_limited' }, { 'retry-after': '1' })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const request = new Request('https://host.test/api/chat/send', {
    method: 'POST',
    body: JSON.stringify({ roomId: 'room1', content: 'hi' })
  });
  const res = await client.handleClientSend(request, 'dave');
  assert.equal(res.status, 429);
  const body = (await res.json()) as { error: string };
  assert.equal(body.error, 'rate_limited');
});

test('handleClientSendBody is the Express-friendly equivalent', async () => {
  const { fetchImpl } = createRoutedFetch({
    'POST /rooms/room1/messages': () => jsonRes(202, { seq: 1, time: 't' })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const result = await client.handleClientSendBody({ roomId: 'room1', content: 'hi' }, 'dave');
  assert.equal(result.status, 202);
  assert.deepEqual(result.body, { seq: 1, time: 't' });
});

test('handlePresendWebhook verifies the signature and runs the configured presend hook', async () => {
  const client = new DotwireHostClient(
    baseConfig({ webhookSecret: 'shh', presend: (ctx) => ({ allow: true, content: `${ctx.content}!` }) })
  );

  const bodyText = JSON.stringify({ roomId: 'room1', senderId: 'alice', content: 'hi', time: 't', source: 'member' });
  const { createHmac } = await import('node:crypto');
  const sig = `sha256=${createHmac('sha256', 'shh').update(bodyText).digest('hex')}`;

  const goodRequest = new Request('https://host.test/webhooks/presend', {
    method: 'POST',
    headers: { 'X-Dotwire-Signature': sig, 'X-Dotwire-Event': 'presend' },
    body: bodyText
  });
  const res = await client.handlePresendWebhook(goodRequest);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { allow: true, content: 'hi!' });

  const badRequest = new Request('https://host.test/webhooks/presend', {
    method: 'POST',
    headers: { 'X-Dotwire-Signature': 'sha256=deadbeef', 'X-Dotwire-Event': 'presend' },
    body: bodyText
  });
  const badRes = await client.handlePresendWebhook(badRequest);
  assert.equal(badRes.status, 401);
});

test('handlePresendWebhook with no secret configured skips signature verification', async () => {
  const client = new DotwireHostClient(baseConfig({ presend: () => ({ allow: false, rejectionReason: 'blocked' }) }));
  const bodyText = JSON.stringify({ roomId: 'room1', senderId: 'alice', content: 'hi', time: 't', source: 'member' });
  const res = await client.handlePresendWebhook(new Request('https://host.test/webhooks/presend', { method: 'POST', body: bodyText }));
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { allow: false, reason: 'blocked' });
});

test('handlePresendWebhook defaults to allow when no presend hook is configured', async () => {
  const client = new DotwireHostClient(baseConfig());
  const bodyText = JSON.stringify({ roomId: 'room1', senderId: 'alice', content: 'hi', time: 't', source: 'member' });
  const res = await client.handlePresendWebhook(new Request('https://host.test/webhooks/presend', { method: 'POST', body: bodyText }));
  const body = (await res.json()) as { allow: boolean };
  assert.equal(body.allow, true);
});

test('handlePostSendWebhook runs the configured postSend hook and always answers {}', async () => {
  let seen: unknown;
  const client = new DotwireHostClient(
    baseConfig({
      postSend: (ctx) => {
        seen = ctx;
      }
    })
  );
  const bodyText = JSON.stringify({ roomId: 'room1', seq: 9, senderId: 'alice', content: 'hi', time: 't', source: 'member' });
  const res = await client.handlePostSendWebhook(new Request('https://host.test/webhooks/postsend', { method: 'POST', body: bodyText }));
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), {});
  assert.deepEqual(seen, { roomId: 'room1', seq: 9, senderUserId: 'alice', content: 'hi', time: 't' });
});

test('handlePostSendWebhookBody rejects a bad signature with 401', async () => {
  const client = new DotwireHostClient(baseConfig({ webhookSecret: 'shh' }));
  const body = { roomId: 'room1', seq: 1, senderId: 'a', content: 'hi', time: 't', source: 'member' };
  const result = await client.handlePostSendWebhookBody(body, 'sha256=nope');
  assert.equal(result.status, 401);
});
