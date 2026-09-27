import { toDotwireClientError } from './errors.js';

interface TokenResponseBody {
  token: string;
}

/**
 * Fetches `tokenUrl`, decodes `exp` from the JWT payload without verifying (this is only used
 * to schedule refresh, never for trust), caches the token, and refetches `marginMs` before
 * expiry (spec §6). Concurrent callers during a refresh share the same in-flight fetch.
 */
export class TokenManager {
  private cachedToken: string | null = null;
  private cachedExpMs: number | null = null;
  private pending: Promise<string> | null = null;

  constructor(
    private readonly tokenUrl: string,
    private readonly marginMs: number,
    private readonly fetchImpl: typeof fetch = fetch
  ) {}

  public async getToken(): Promise<string> {
    if (this.cachedToken !== null && !this.isExpiringSoon()) {
      return this.cachedToken;
    }
    if (!this.pending) {
      this.pending = this.fetchAndCache().finally(() => {
        this.pending = null;
      });
    }
    return this.pending;
  }

  private isExpiringSoon(): boolean {
    if (this.cachedExpMs === null) return false;
    return Date.now() >= this.cachedExpMs - this.marginMs;
  }

  private async fetchAndCache(): Promise<string> {
    const response = await this.fetchImpl(this.tokenUrl, { credentials: 'include' });
    if (!response.ok) {
      throw await toDotwireClientError(response);
    }
    const body = (await response.json()) as TokenResponseBody;
    this.cachedToken = body.token;
    this.cachedExpMs = decodeExpMs(body.token);
    return this.cachedToken;
  }
}

/** Base64url-decodes the JWT payload segment and reads `exp`, in milliseconds. No signature check. */
export function decodeExpMs(token: string): number | null {
  const parts = token.split('.');
  if (parts.length < 2) return null;
  try {
    const payload = JSON.parse(base64UrlDecode(parts[1])) as Record<string, unknown>;
    return typeof payload.exp === 'number' ? payload.exp * 1000 : null;
  } catch {
    return null;
  }
}

function base64UrlDecode(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
  if (typeof Buffer !== 'undefined') {
    return Buffer.from(padded, 'base64').toString('utf-8');
  }
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  return new TextDecoder('utf-8').decode(bytes);
}
