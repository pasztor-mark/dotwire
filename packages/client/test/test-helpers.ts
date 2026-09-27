import { HttpTransportType, type HubConnection, type IHttpConnectionOptions } from '@microsoft/signalr';
import { DotwireClient } from '../src/client.js';
import type { DotwireClientOptions } from '../src/types.js';
import { FakeHubConnection } from './fake-hub-connection.js';

export interface TestClientResult {
  client: DotwireClient;
  fake: FakeHubConnection;
  lastFactoryUrl: string | null;
  lastFactoryOptions: IHttpConnectionOptions | null;
}

export function createTestClient(overrides: Partial<DotwireClientOptions> = {}): TestClientResult {
  const fake = new FakeHubConnection();
  const result: TestClientResult = { client: undefined as unknown as DotwireClient, fake, lastFactoryUrl: null, lastFactoryOptions: null };

  const options: DotwireClientOptions = {
    baseUrl: 'https://dotwire.test',
    getAccessToken: () => 'test-token',
    connectionFactory: (url: string, httpOptions: IHttpConnectionOptions): HubConnection => {
      result.lastFactoryUrl = url;
      result.lastFactoryOptions = httpOptions;
      return fake.asHubConnection();
    },
    ...overrides
  };

  result.client = new DotwireClient(options);
  return result;
}

/** Builds an unsigned JWT-shaped string with the given payload, for exp-decoding tests. */
export function fakeJwt(payload: Record<string, unknown>): string {
  const header = base64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }));
  const body = base64Url(JSON.stringify(payload));
  return `${header}.${body}.`;
}

function base64Url(input: string): string {
  return Buffer.from(input, 'utf-8').toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function jsonResponse(body: unknown, init: { status?: number; headers?: Record<string, string> } = {}): Response {
  return new Response(JSON.stringify(body), {
    status: init.status ?? 200,
    headers: { 'Content-Type': 'application/json', ...(init.headers ?? {}) }
  });
}

export { HttpTransportType };
