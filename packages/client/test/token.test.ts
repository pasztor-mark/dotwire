import test from 'node:test';
import assert from 'node:assert/strict';
import { TokenManager } from '../src/token.js';
import { fakeJwt, jsonResponse } from './test-helpers.js';

test('TokenManager caches the token and refetches tokenRefreshMarginMs before expiry', async (t) => {
  const now = Math.floor(Date.now() / 1000);
  let fetchCount = 0;

  t.mock.method(globalThis, 'fetch', async (url: string, init: RequestInit) => {
    fetchCount += 1;
    assert.equal(url, 'https://dotwire.test/token');
    assert.equal(init.credentials, 'include');
    // First call: comfortably outside the refresh margin; second would be a longer-lived token.
    const exp = fetchCount === 1 ? now + 30 : now + 3600;
    return jsonResponse({ token: fakeJwt({ exp, sub: 'alice' }) });
  });

  const manager = new TokenManager('https://dotwire.test/token', 500);

  const first = await manager.getToken();
  assert.equal(fetchCount, 1);

  // Well within the (short) expiry margin - should still return the cached token, no refetch.
  const second = await manager.getToken();
  assert.equal(first, second);
  assert.equal(fetchCount, 1);
});

test('TokenManager refetches once the cached token is within the refresh margin', async (t) => {
  const now = Math.floor(Date.now() / 1000);
  let fetchCount = 0;

  t.mock.method(globalThis, 'fetch', async () => {
    fetchCount += 1;
    // exp is already inside any reasonable margin - forces a refetch on every call.
    const exp = now + 1;
    return jsonResponse({ token: fakeJwt({ exp, n: fetchCount }) });
  });

  const manager = new TokenManager('https://dotwire.test/token', 60_000);

  const first = await manager.getToken();
  assert.equal(fetchCount, 1);

  const second = await manager.getToken();
  assert.equal(fetchCount, 2);
  assert.notEqual(first, second);
});

test('TokenManager surfaces a DotwireClientError when the token endpoint fails', async (t) => {
  t.mock.method(globalThis, 'fetch', async () =>
    new Response(JSON.stringify({ error: 'unauthorized' }), { status: 401 })
  );

  const manager = new TokenManager('https://dotwire.test/token', 30_000);

  await assert.rejects(() => manager.getToken(), (err: unknown) => {
    assert.ok(err && typeof err === 'object' && 'kind' in err);
    assert.equal((err as { kind: string }).kind, 'Unauthorized');
    return true;
  });
});
