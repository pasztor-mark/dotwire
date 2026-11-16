export type DotwireErrorKind =
  | 'BadRequest'
  | 'Unauthorized'
  | 'Forbidden'
  | 'NotFound'
  | 'PresendRejected'
  | 'RateLimited'
  | 'Unavailable'
  | 'Unknown';

export interface DotwireClientErrorInit {
  status: number;
  kind: DotwireErrorKind;
  code?: string;
  reason?: string;
  retryAfterMs?: number;
}

/**
 * Client-side error type, matching the `{status, kind, code?, reason?, retryAfterMs?}` shape
 * shared with `Dotwire.Host` and `@dotwire/host` (spec §6, §4.6, §5).
 */
export class DotwireClientError extends Error {
  public readonly status: number;
  public readonly kind: DotwireErrorKind;
  public readonly code?: string;
  public readonly reason?: string;
  public readonly retryAfterMs?: number;

  constructor(init: DotwireClientErrorInit) {
    super(init.reason ?? init.code ?? `dotwire request failed with status ${init.status}`);
    this.name = 'DotwireClientError';
    this.status = init.status;
    this.kind = init.kind;
    this.code = init.code;
    this.reason = init.reason;
    this.retryAfterMs = init.retryAfterMs;
  }
}

/**
 * Maps a failed fetch `Response` to a {@link DotwireClientError}. New 4xx/5xx codes carry a
 * `{error, reason?}` JSON body (spec §3.14, `dotwire/Api/ApiResults.cs`); bare 401/403/404
 * responses have no body and are classified by status alone.
 */
export async function toDotwireClientError(response: Response): Promise<DotwireClientError> {
  const status = response.status;
  let code: string | undefined;
  let reason: string | undefined;

  try {
    const body: unknown = await response.json();
    if (body && typeof body === 'object') {
      const record = body as Record<string, unknown>;
      if (typeof record.error === 'string') code = record.error;
      if (typeof record.reason === 'string') reason = record.reason;
    }
  } catch {
    // Bare error body (e.g. 401/403/404) or non-JSON payload.
  }

  let retryAfterMs: number | undefined;
  const retryAfterHeader = response.headers?.get?.('Retry-After');
  if (retryAfterHeader) {
    const seconds = Number(retryAfterHeader);
    if (!Number.isNaN(seconds)) {
      retryAfterMs = seconds * 1000;
    }
  }

  return new DotwireClientError({ status, kind: classifyKind(status, code), code, reason, retryAfterMs });
}

function classifyKind(status: number, code?: string): DotwireErrorKind {
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

const HUB_EXCEPTION_MARKER = 'HubException: ';

function hubErrorText(error: unknown): string {
  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  const at = message.lastIndexOf(HUB_EXCEPTION_MARKER);
  return at === -1 ? message : message.slice(at + HUB_EXCEPTION_MARKER.length);
}

export function isHubSendUnavailable(error: unknown): boolean {
  const text = hubErrorText(error);
  return text.split(':', 1)[0].trim() === 'HubSendDisabled' || text.includes('does not exist');
}

export function toDotwireHubError(error: unknown): DotwireClientError {
  if (error instanceof DotwireClientError) return error;
  const text = hubErrorText(error).trim();
  const colon = text.indexOf(':');
  const code = colon === -1 ? text : text.slice(0, colon);
  const reason = colon === -1 ? undefined : text.slice(colon + 1).trim() || undefined;

  if (code === 'Forbidden') return new DotwireClientError({ status: 403, kind: 'Forbidden', code });
  if (code === 'RateLimited') return new DotwireClientError({ status: 429, kind: 'RateLimited', code });
  if (code.startsWith('invalid_')) return new DotwireClientError({ status: 400, kind: 'BadRequest', code, reason });
  if (code === 'presend_rejected') return new DotwireClientError({ status: 422, kind: 'PresendRejected', code, reason });
  if (code === 'presend_unavailable' || code === 'stream_unavailable') {
    return new DotwireClientError({ status: 503, kind: 'Unavailable', code, reason });
  }
  return new DotwireClientError({ status: 0, kind: 'Unknown', code: code || undefined, reason: reason ?? (code ? undefined : text) });
}
