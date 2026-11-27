import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { DotwireHostClient } from '../src/client.js';
import { auditCanonicalForm, auditChainHash, GENESIS_HASH } from '../src/audit-chain.js';
import type { AuditEntry } from '../src/types.js';
import { baseConfig, createRoutedFetch, jsonRes } from './helpers.js';

const fixturePath = fileURLToPath(new URL('../../../../dotwire.Tests/Fixtures/audit-chain.json', import.meta.url));
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as AuditEntry[];
const ROOM = '11111111-1111-4111-8111-111111111111';

test('audit chain: the shared fixture verifies to the server-produced hashes', () => {
  let prev = GENESIS_HASH;
  for (const row of fixture) {
    prev = auditChainHash(prev, row);
    assert.equal(prev, row.hash, `id ${row.id}`);
  }
});

test('audit chain: a non-UTC offset is rejected', () => {
  const row = { ...fixture[0]!, time: '2026-09-07T14:34:56.123456+02:00' };
  assert.throws(() => auditCanonicalForm(row), /not a UTC timestamp/);
});

test('archiveAuditLog: a tampered value fails naming its id, earlier lines stay written', async () => {
  const tampered = fixture.map((row) => (row.id === 3 ? { ...row, value: 31 } : row));
  const { fetchImpl } = createRoutedFetch({
    'GET /audit': () => jsonRes(200, { events: tampered, hasMore: false })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const lines: string[] = [];
  await assert.rejects(
    client.archiveAuditLog((line) => {
      lines.push(line);
    }),
    /id 3/
  );
  assert.equal(lines.length, 2);
});

test('archiveAuditLog: writes verified rows then checkpoint lines', async () => {
  const { fetchImpl } = createRoutedFetch({
    'GET /audit': () => jsonRes(200, { events: fixture, hasMore: false }),
    'GET /audit/checkpoints': () =>
      jsonRes(200, { checkpoints: [{ day: '2026-09-07', lastId: 3, hash: fixture[2]!.hash, createdAt: '2026-09-07T12:37:00Z' }] })
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const lines: string[] = [];
  const result = await client.archiveAuditLog((line) => {
    lines.push(line);
  });
  assert.deepEqual(result, { count: 3, lastId: 3, lastHash: fixture[2]!.hash });
  assert.equal(lines.length, 4);
  assert.ok(lines[3]!.startsWith('{"checkpoint":'));
});

test('archiveAuditLog: afterId above zero without previousHash is an error', async () => {
  const client = new DotwireHostClient(baseConfig({ fetch: createRoutedFetch({}).fetchImpl }));
  await assert.rejects(client.archiveAuditLog(() => {}, { afterId: 3 }), /previousHash/);
});

test('exportRoomHistory pages forward oldest-first and returns the count', async () => {
  const seqMessage = (seq: number) => ({ seq, time: '2026-09-07T12:00:00Z', senderId: 'dave', content: `m${seq}` });
  const pages: Record<string, { messages: ReturnType<typeof seqMessage>[]; hasMore: boolean }> = {
    '0': { messages: [seqMessage(1), seqMessage(2)], hasMore: true },
    '2': { messages: [seqMessage(3), seqMessage(4)], hasMore: true },
    '4': { messages: [seqMessage(5)], hasMore: false }
  };
  const { fetchImpl, calls } = createRoutedFetch({
    [`GET /rooms/${ROOM}/messages`]: (call) => {
      const afterSeq = new URL(call.url).searchParams.get('afterSeq') ?? '';
      return jsonRes(200, { roomId: ROOM, ...pages[afterSeq] });
    }
  });
  const client = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const lines: string[] = [];
  const count = await client.exportRoomHistory(ROOM, 'dave', (line) => {
    lines.push(line);
  });
  assert.equal(count, 5);
  assert.deepEqual(lines.map((line) => (JSON.parse(line) as { seq: number }).seq), [1, 2, 3, 4, 5]);
  assert.deepEqual(
    calls.map((call) => new URL(call.url).searchParams.get('afterSeq')),
    ['0', '2', '4']
  );
});
