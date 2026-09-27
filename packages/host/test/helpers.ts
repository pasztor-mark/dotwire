import type { DotwireHostConfig } from '../src/types.js';
import { TEST_PRIVATE_KEY_PEM } from './keys.js';

export interface RecordedCall {
  method: string;
  url: string;
  headers: Record<string, string>;
  body?: string;
}

export type FetchHandler = (call: RecordedCall) => Response | Promise<Response>;

/** A minimal fake `fetch` that records every call and answers via `handler`. */
export function createFakeFetch(handler: FetchHandler): { fetchImpl: typeof fetch; calls: RecordedCall[] } {
  const calls: RecordedCall[] = [];
  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' ? input : input.toString();
    const method = (init?.method ?? 'GET').toUpperCase();
    const headers: Record<string, string> = {};
    if (init?.headers) {
      new Headers(init.headers as HeadersInit).forEach((v, k) => {
        headers[k] = v;
      });
    }
    const body = typeof init?.body === 'string' ? init.body : undefined;
    const call: RecordedCall = { method, url, headers, body };
    calls.push(call);
    return handler(call);
  }) as typeof fetch;
  return { fetchImpl, calls };
}

/** A fetch that answers a fixed sequence of route handlers, keyed by "METHOD pathname". */
export function createRoutedFetch(routes: Record<string, FetchHandler>): { fetchImpl: typeof fetch; calls: RecordedCall[] } {
  return createFakeFetch((call) => {
    const pathname = new URL(call.url).pathname;
    const key = `${call.method} ${pathname}`;
    const route = routes[key];
    if (!route) {
      throw new Error(`no fake route for ${key} (had: ${Object.keys(routes).join(', ')})`);
    }
    return route(call);
  });
}

export function jsonRes(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
}

export function baseConfig(overrides: Partial<DotwireHostConfig> = {}): DotwireHostConfig {
  return {
    baseUrl: 'https://dotwire.test',
    issuer: 'https://issuer.test',
    keyId: 'test-key',
    privateKeyPem: TEST_PRIVATE_KEY_PEM,
    ...overrides
  };
}
