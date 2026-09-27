import { DotwireApiError } from './errors.js';
import type { DotwireHostClient } from './client.js';
import type { PostSendContext } from './types.js';
import { delay } from './util.js';

/** An accepted message awaiting asynchronous review. (roomId, seq) is the redaction target. */
export interface PendingModeration {
  roomId: string;
  seq: number;
  time: string;
  senderUserId: string;
  content: string;
}

/** One classifier decision. Only flagged verdicts trigger an action. */
export interface ModerationVerdict {
  roomId: string;
  seq: number;
  flagged: boolean;
  category?: string;
  reason?: string;
}

/**
 * The expensive judgment call, run off the send path over a whole batch at once. Implementations
 * call an LLM (see `samples/moderation` for a Claude Message Batches API reference implementation),
 * a third-party moderation API, or anything else too slow to sit inside a send. Return one verdict
 * per input; inputs with no verdict are treated as not flagged.
 */
export interface ModerationClassifier {
  classify(batch: readonly PendingModeration[], signal?: AbortSignal): Promise<readonly ModerationVerdict[]>;
}

/**
 * In-process hand-off between the send path and the {@link BatchModerationWorker}. Enqueueing
 * (via {@link postSendHook}) is a microtask-cheap array push, so wiring it into
 * `DotwireHostConfig.postSend` adds no meaningful latency to a send. The verdict itself is
 * computed later, in batches, by the worker.
 *
 * Bounded at `capacity`: once full, {@link enqueue} (and therefore `postSendHook`) waits for
 * room rather than silently dropping - the queue applies back-pressure to sends, mirroring the
 * .NET port's `BoundedChannelFullMode.Wait` default.
 */
export class ModerationQueue {
  private readonly items: PendingModeration[] = [];
  private readonly waitingReaders: Array<() => void> = [];
  private readonly waitingWriters: Array<() => void> = [];
  private completed = false;

  private readonly capacity: number;

  constructor(capacity: number = 10_000) {
    this.capacity = capacity;
  }

  async enqueue(item: PendingModeration, signal?: AbortSignal): Promise<void> {
    if (this.completed) throw new Error('ModerationQueue is already completed');
    while (this.items.length >= this.capacity) {
      await this.waitForWriteSlot(signal);
    }
    this.items.push(item);
    this.wake(this.waitingReaders);
  }

  /** Assign directly: `client.postSend = queue.postSendHook`. */
  postSendHook = (ctx: PostSendContext): Promise<void> =>
    this.enqueue({ roomId: ctx.roomId, seq: ctx.seq, time: ctx.time, senderUserId: ctx.senderUserId, content: ctx.content });

  /** Signals the worker to drain what is queued and stop. */
  complete(): void {
    this.completed = true;
    this.wake(this.waitingReaders);
  }

  /** Blocks for an item; resolves null once completed and drained, or if `signal` fires. */
  async dequeue(signal?: AbortSignal): Promise<PendingModeration | null> {
    while (this.items.length === 0) {
      if (this.completed || signal?.aborted) return null;
      await new Promise<void>((resolve) => {
        const onAbort = () => resolve();
        signal?.addEventListener('abort', onAbort, { once: true });
        this.waitingReaders.push(() => {
          signal?.removeEventListener('abort', onAbort);
          resolve();
        });
      });
    }
    return this.take();
  }

  /** Waits up to `timeoutMs` for an item. Resolves 'timeout' on expiry, null on completion/abort. */
  async tryDequeue(timeoutMs: number, signal?: AbortSignal): Promise<PendingModeration | null | 'timeout'> {
    if (this.items.length > 0) return this.take();
    if (this.completed || signal?.aborted) return null;

    return new Promise((resolve) => {
      let settled = false;
      const finish = (value: PendingModeration | null | 'timeout') => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener('abort', onAbort);
        resolve(value);
      };
      const timer = setTimeout(() => finish('timeout'), timeoutMs);
      const onAbort = () => finish(null);
      const wake = (): void => {
        if (settled) return;
        if (this.items.length > 0) finish(this.take());
        else if (this.completed) finish(null);
        else this.waitingReaders.push(wake);
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      this.waitingReaders.push(wake);
    });
  }

  private take(): PendingModeration {
    const item = this.items.shift()!;
    const writer = this.waitingWriters.shift();
    if (writer) writer();
    return item;
  }

  private waitForWriteSlot(signal?: AbortSignal): Promise<void> {
    return new Promise((resolve) => {
      const onAbort = () => resolve();
      signal?.addEventListener('abort', onAbort, { once: true });
      this.waitingWriters.push(() => {
        signal?.removeEventListener('abort', onAbort);
        resolve();
      });
    });
  }

  private wake(waiters: Array<() => void>): void {
    const pending = waiters.splice(0);
    for (const w of pending) w();
  }
}

export interface BatchModerationOptions {
  /** Flush when this many messages are queued... Default 100. */
  maxBatchSize?: number;
  /** ...or when the oldest queued message has waited this long, whichever comes first. Default 30000. */
  maxBatchDelayMs?: number;
  /** Classifier attempts per batch (exponential backoff between them) before the batch is dropped. Default 3. */
  maxClassifierAttempts?: number;
}

/**
 * Drains a {@link ModerationQueue} in size-or-time bounded batches, hands each batch to the
 * {@link ModerationClassifier}, and runs `onFlagged` for every flagged verdict (typically
 * {@link BatchModerationWorker.redactWith}). Runs entirely off the send path.
 */
export class BatchModerationWorker {
  private readonly queue: ModerationQueue;
  private readonly classifier: ModerationClassifier;
  private readonly onFlagged: (verdict: ModerationVerdict, signal?: AbortSignal) => Promise<void> | void;
  private readonly onError?: (err: unknown) => void;
  private readonly maxBatchSize: number;
  private readonly maxBatchDelayMs: number;
  private readonly maxClassifierAttempts: number;

  constructor(
    queue: ModerationQueue,
    classifier: ModerationClassifier,
    onFlagged: (verdict: ModerationVerdict, signal?: AbortSignal) => Promise<void> | void,
    options: BatchModerationOptions = {},
    onError?: (err: unknown) => void
  ) {
    this.queue = queue;
    this.classifier = classifier;
    this.onFlagged = onFlagged;
    this.onError = onError;
    this.maxBatchSize = options.maxBatchSize ?? 100;
    this.maxBatchDelayMs = options.maxBatchDelayMs ?? 30_000;
    this.maxClassifierAttempts = options.maxClassifierAttempts ?? 3;
  }

  /** The usual action: physically redact the flagged message through the admin API, honouring `Retry-After` on 429. */
  static redactWith(host: DotwireHostClient): (verdict: ModerationVerdict) => Promise<void> {
    return async (verdict) => {
      const maxAttempts = 3;
      for (let attempt = 1; attempt <= maxAttempts; attempt++) {
        try {
          await host.redactMessage(verdict.roomId, verdict.seq);
          return;
        } catch (err) {
          if (err instanceof DotwireApiError && err.status === 429 && attempt < maxAttempts) {
            await delay(err.retryAfterMs ?? 1000 * attempt);
            continue;
          }
          throw err;
        }
      }
    };
  }

  /** Runs until `signal` fires or the queue is completed and drained. */
  async run(signal: AbortSignal): Promise<void> {
    while (true) {
      const batch = await this.fillBatch(signal);
      if (batch.length === 0) return;
      await this.processBatch(batch, signal);
      if (signal.aborted) return;
    }
  }

  private async fillBatch(signal: AbortSignal): Promise<PendingModeration[]> {
    const batch: PendingModeration[] = [];
    const first = await this.queue.dequeue(signal);
    if (first === null) return batch;
    batch.push(first);

    const deadline = Date.now() + this.maxBatchDelayMs;
    while (batch.length < this.maxBatchSize) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) break;
      const item = await this.queue.tryDequeue(remaining, signal);
      if (item === 'timeout' || item === null) break;
      batch.push(item);
    }
    return batch;
  }

  private async processBatch(batch: PendingModeration[], signal: AbortSignal): Promise<void> {
    let verdicts: readonly ModerationVerdict[] | undefined;
    for (let attempt = 1; attempt <= this.maxClassifierAttempts; attempt++) {
      try {
        verdicts = await this.classifier.classify(batch, signal);
        break;
      } catch (err) {
        if (signal.aborted) return;
        this.onError?.(err);
        if (attempt === this.maxClassifierAttempts) return; // best-effort: drop the batch, don't wedge the worker
        await delay(1000 * Math.pow(2, attempt), signal).catch(() => undefined);
      }
    }

    if (!verdicts) return;

    for (const verdict of verdicts) {
      if (!verdict.flagged) continue;
      try {
        await this.onFlagged(verdict, signal);
      } catch (err) {
        if (signal.aborted) return;
        this.onError?.(err);
      }
    }
  }
}
