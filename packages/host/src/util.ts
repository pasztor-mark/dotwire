/** Resolves after `ms`, or rejects with an AbortError if `signal` fires first. */
export function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    const timer = setTimeout(() => {
      cleanup();
      resolve();
    }, ms);
    const onAbort = () => {
      cleanup();
      reject(new DOMException('Aborted', 'AbortError'));
    };
    const cleanup = () => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', onAbort);
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/** True if `err` represents cancellation via AbortSignal (fetch's AbortError, or our own {@link delay}). */
export function isAbortError(err: unknown): boolean {
  return err instanceof Error && err.name === 'AbortError';
}
