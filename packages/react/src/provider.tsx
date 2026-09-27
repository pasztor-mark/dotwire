import { createContext, useEffect, useState, type ReactNode } from 'react';
import { DotwireClient, type DotwireClientOptions } from '@dotwire/client';

export const DotwireContext = createContext<DotwireClient | null>(null);

export interface DotwireProviderProps {
  children?: ReactNode;
  /**
   * Options to construct a `DotwireClient` internally. Ignored if `client` is also given.
   * Construct this once (e.g. with `useMemo`/`useState`, or a module-level constant) — a new
   * object identity on every render tears down and rebuilds the client.
   */
  options?: DotwireClientOptions;
  /**
   * A pre-built `DotwireClient` instance. When given, the provider never constructs or
   * disposes a client itself — the caller owns that instance's lifecycle.
   */
  client?: DotwireClient;
}

/**
 * Provides a `DotwireClient` to `useDotwireClient`/`useConnectionState`/`useRoom`/`useTyping`/
 * `usePresence` (spec §7).
 *
 * - Construction happens in an effect, never during render, so this component is safe to
 *   render on the server (`react-dom/server`) — `useDotwireClient()` simply returns `null`
 *   until the effect runs on the client. See `test/ssr.test.tsx`.
 * - In React 18 StrictMode, effects run an extra setup+cleanup pass in development; the
 *   cleanup below disposes whatever instance that pass constructed before the next one runs,
 *   so double-invocation is safe.
 * - A client built from `options` is disposed on unmount (or when `options` changes). A
 *   client passed via the `client` prop is never disposed here — the caller owns it.
 */
export function DotwireProvider(props: DotwireProviderProps): ReactNode {
  const { children, options, client: providedClient } = props;
  const [managedClient, setManagedClient] = useState<DotwireClient | null>(null);

  useEffect(() => {
    if (providedClient || !options) {
      return undefined;
    }

    const instance = new DotwireClient(options);
    setManagedClient(instance);

    return () => {
      instance.dispose();
      setManagedClient((current) => (current === instance ? null : current));
    };
    // `options` is compared by reference deliberately: a new object rebuilds the client.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [providedClient, options]);

  const client = providedClient ?? managedClient;

  return <DotwireContext.Provider value={client}>{children}</DotwireContext.Provider>;
}
