import type { SendMessageResult } from './types.js';

/**
 * Mirrors `Dotwire.Host.DotwireApiException.Kind` (.NET) / the wire error contract in spec §3.14.
 * Mapped from HTTP status first, then the `error` code in the body.
 */
export type DotwireApiErrorKind =
  | 'BadRequest'
  | 'Unauthorized'
  | 'Forbidden'
  | 'NotFound'
  | 'PresendRejected'
  | 'RateLimited'
  | 'Unavailable'
  | 'Unknown';

export interface DotwireApiErrorInit {
  status: number;
  kind: DotwireApiErrorKind;
  code?: string;
  reason?: string;
  retryAfterMs?: number;
  method: string;
  path: string;
}

/** Thrown when the dotwire server answers a request with a non-success status. */
export class DotwireApiError extends Error {
  readonly status: number;
  readonly kind: DotwireApiErrorKind;
  readonly code?: string;
  readonly reason?: string;
  readonly retryAfterMs?: number;
  readonly method: string;
  readonly path: string;

  constructor(init: DotwireApiErrorInit) {
    super(`dotwire ${init.method} ${init.path} failed: ${init.status}${init.code ? ` ${init.code}` : ''}`);
    this.name = 'DotwireApiError';
    this.status = init.status;
    this.kind = init.kind;
    this.code = init.code;
    this.reason = init.reason;
    this.retryAfterMs = init.retryAfterMs;
    this.method = init.method;
    this.path = init.path;
  }
}

/**
 * Maps an HTTP status (and, where ambiguous, the wire error code from spec §3.14) to a
 * {@link DotwireApiErrorKind}. Status is authoritative; code only disambiguates 422/503/429/400.
 */
export function mapErrorKind(status: number, code?: string): DotwireApiErrorKind {
  switch (status) {
    case 400:
      return 'BadRequest';
    case 401:
      return 'Unauthorized';
    case 403:
      return 'Forbidden';
    case 404:
      return 'NotFound';
    case 422:
      return code === 'presend_rejected' ? 'PresendRejected' : 'BadRequest';
    case 429:
      return 'RateLimited';
    case 503:
      return 'Unavailable';
    default:
      return 'Unknown';
  }
}

/**
 * Thrown when the host's own {@link DotwireHostConfig.presend} hook rejects a message, evaluated
 * in-process before the message ever reaches the wire (no network round-trip). Distinct from a
 * {@link DotwireApiError} with kind `PresendRejected`, which is the *server's* configured presend
 * webhook rejecting a message on `POST /rooms/{roomId}/messages` (a different, server-side hook).
 */
export class PresendRejectedError extends Error {
  readonly reason: string;

  constructor(reason: string) {
    super(reason);
    this.name = 'PresendRejectedError';
    this.reason = reason;
  }
}

/**
 * The message WAS sent (see {@link sent}); only the local `postSend` hook failed afterwards.
 * Kept distinct from a send failure so callers never retry a send that already succeeded.
 */
export class PostSendError extends Error {
  readonly sent: SendMessageResult;

  constructor(sent: SendMessageResult, cause: unknown) {
    super('The message was sent, but the postSend hook failed.', { cause });
    this.name = 'PostSendError';
    this.sent = sent;
  }
}
