import test from 'node:test';
import assert from 'node:assert/strict';
import { BatchModerationWorker, ModerationQueue, type ModerationClassifier, type ModerationVerdict, type PendingModeration } from '../src/moderation.js';
import { DotwireHostClient } from '../src/client.js';
import { DotwireApiError } from '../src/errors.js';
import { baseConfig, createFakeFetch, createRoutedFetch, jsonRes } from './helpers.js';

function pending(seq: number): PendingModeration {
  return { roomId: 'r1', seq, time: 't', senderUserId: 'u', content: `msg ${seq}` };
}

test('ModerationQueue: dequeue blocks until an item is enqueued, then delivers it', async () => {
  const queue = new ModerationQueue(10);
  const dequeued = queue.dequeue();
  await queue.enqueue(pending(1));
  assert.deepEqual(await dequeued, pending(1));
});

test('ModerationQueue: complete() drains remaining items then signals done with null', async () => {
  const queue = new ModerationQueue(10);
  await queue.enqueue(pending(1));
  queue.complete();
  assert.deepEqual(await queue.dequeue(), pending(1));
  assert.equal(await queue.dequeue(), null);
});

test('ModerationQueue: enqueue applies back-pressure at capacity', async () => {
  const queue = new ModerationQueue(1);
  await queue.enqueue(pending(1));
  let secondResolved = false;
  const secondEnqueue = queue.enqueue(pending(2)).then(() => {
    secondResolved = true;
  });
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(secondResolved, false, 'enqueue should block while the queue is full');

  await queue.dequeue(); // frees a slot
  await secondEnqueue;
  assert.equal(secondResolved, true);
});

test('ModerationQueue.postSendHook enqueues from a PostSendContext', async () => {
  const queue = new ModerationQueue(10);
  await queue.postSendHook({ roomId: 'r1', seq: 3, time: 't', content: 'hi', senderUserId: 'u1' });
  assert.deepEqual(await queue.dequeue(), { roomId: 'r1', seq: 3, time: 't', senderUserId: 'u1', content: 'hi' });
});

test('BatchModerationWorker flushes a batch once maxBatchSize is reached, without waiting for the delay', async () => {
  const queue = new ModerationQueue(100);
  const seenBatches: PendingModeration[][] = [];
  const classifier: ModerationClassifier = {
    async classify(batch) {
      seenBatches.push([...batch]);
      return batch.map((m) => ({ roomId: m.roomId, seq: m.seq, flagged: m.seq % 2 === 0 }));
    }
  };
  const flagged: ModerationVerdict[] = [];
  const worker = new BatchModerationWorker(queue, classifier, (v) => {
    flagged.push(v);
  }, { maxBatchSize: 3, maxBatchDelayMs: 60_000 });

  const controller = new AbortController();
  const runPromise = worker.run(controller.signal);

  for (let seq = 1; seq <= 3; seq++) await queue.enqueue(pending(seq));
  queue.complete();
  await runPromise;

  assert.equal(seenBatches.length, 1);
  assert.equal(seenBatches[0].length, 3);
  assert.deepEqual(flagged.map((f) => f.seq), [2]);
});

test('BatchModerationWorker flushes a partial batch once maxBatchDelayMs elapses', async () => {
  const queue = new ModerationQueue(100);
  const seenBatchSizes: number[] = [];
  const classifier: ModerationClassifier = {
    async classify(batch) {
      seenBatchSizes.push(batch.length);
      return [];
    }
  };
  const worker = new BatchModerationWorker(queue, classifier, () => {}, { maxBatchSize: 100, maxBatchDelayMs: 30 });

  const controller = new AbortController();
  const runPromise = worker.run(controller.signal);
  await queue.enqueue(pending(1));
  // Don't add a second item: the batch should flush on the delay with just one item.
  await new Promise((r) => setTimeout(r, 80));
  queue.complete();
  await runPromise;

  assert.deepEqual(seenBatchSizes, [1]);
});

test('BatchModerationWorker drops a batch and calls onError after exhausting classifier attempts', async () => {
  const queue = new ModerationQueue(100);
  const errors: unknown[] = [];
  const classifier: ModerationClassifier = {
    async classify() {
      throw new Error('classifier down');
    }
  };
  const worker = new BatchModerationWorker(
    queue,
    classifier,
    () => {},
    { maxBatchSize: 1, maxBatchDelayMs: 10_000, maxClassifierAttempts: 1 },
    (err) => errors.push(err)
  );

  const controller = new AbortController();
  const runPromise = worker.run(controller.signal);
  await queue.enqueue(pending(1));
  queue.complete();
  await runPromise;

  assert.equal(errors.length, 1);
});

test('BatchModerationWorker.redactWith retries once on 429 honouring retryAfterMs, then succeeds', async () => {
  let calls = 0;
  const { fetchImpl } = createFakeFetch(() => {
    calls++;
    if (calls === 1) return jsonRes(429, { error: 'rate_limited' }, { 'retry-after': '0' });
    return jsonRes(200, { roomId: 'r1', seq: 5, removedFromHistory: true, removedFromStream: true });
  });
  const host = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const redact = BatchModerationWorker.redactWith(host);
  await redact({ roomId: 'r1', seq: 5, flagged: true });
  assert.equal(calls, 2);
});

test('BatchModerationWorker.redactWith gives up and rethrows after repeated 429s', async () => {
  const { fetchImpl, calls } = createRoutedFetch({
    'DELETE /admin/rooms/r1/messages/5': () => jsonRes(429, { error: 'rate_limited' }, { 'retry-after': '0' })
  });
  const host = new DotwireHostClient(baseConfig({ fetch: fetchImpl }));
  const redact = BatchModerationWorker.redactWith(host);
  await assert.rejects(
    () => redact({ roomId: 'r1', seq: 5, flagged: true }),
    (err: unknown) => err instanceof DotwireApiError && err.status === 429
  );
  assert.equal(calls.length, 3);
});
