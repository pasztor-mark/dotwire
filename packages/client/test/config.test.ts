import test from 'node:test';
import assert from 'node:assert/strict';
import { DotwireClient } from '../src/client.js';

test('constructor throws a clear error when neither tokenUrl nor getAccessToken is given', () => {
  assert.throws(
    () =>
      new DotwireClient({
        baseUrl: 'https://dotwire.test',
        connectionFactory: () => {
          throw new Error('should not be reached');
        }
      } as never),
    /tokenUrl.*getAccessToken/
  );
});
