import { test } from 'node:test';
import assert from 'node:assert/strict';
import { renderToString } from 'react-dom/server';
import { DotwireProvider, useDotwireClient, useConnectionState } from '../src/index.js';

/**
 * A consumer that reads from both hooks. It must render without throwing on the server: no
 * `DotwireClient` should be constructed and no browser API touched during the render pass
 * itself (construction happens in an effect, per `src/provider.tsx`, which never runs during
 * `renderToString`).
 */
function Consumer(): JSX.Element {
  const client = useDotwireClient();
  const connectionState = useConnectionState();
  return (
    <div data-has-client={client !== null} data-connection-state={connectionState}>
      dotwire
    </div>
  );
}

test('DotwireProvider with options renders on the server without constructing a client', () => {
  const html = renderToString(
    <DotwireProvider options={{ baseUrl: 'https://dotwire.test', getAccessToken: () => 'token' }}>
      <Consumer />
    </DotwireProvider>
  );

  assert.match(html, /dotwire/);
  // The effect that would construct DotwireClient never runs during renderToString, so
  // useDotwireClient() must have observed null.
  assert.match(html, /data-has-client="false"/);
  assert.match(html, /data-connection-state="disconnected"/);
});

test('DotwireProvider with no client/options at all still renders on the server', () => {
  const html = renderToString(
    <DotwireProvider>
      <Consumer />
    </DotwireProvider>
  );

  assert.match(html, /dotwire/);
  assert.match(html, /data-has-client="false"/);
});
