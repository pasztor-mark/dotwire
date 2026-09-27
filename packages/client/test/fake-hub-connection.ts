import { HubConnectionState } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';

type Handler = (...args: unknown[]) => void;

/**
 * Minimal fake satisfying the subset of `HubConnection` the client actually calls: `on`,
 * `off`, `invoke`, `start`, `stop`, `state`, and the lifecycle registration methods
 * `onclose`/`onreconnecting`/`onreconnected` (spec §10.3). Typed loosely and cast to
 * `HubConnection` at the call site.
 */
export class FakeHubConnection {
  public state: HubConnectionState = HubConnectionState.Disconnected;
  public serverTimeoutInMilliseconds = 0;
  public keepAliveIntervalInMilliseconds = 0;
  public readonly invokeCalls: Array<{ method: string; args: unknown[] }> = [];
  public invokeImpl: (method: string, ...args: unknown[]) => unknown = () => undefined;

  private readonly handlers = new Map<string, Set<Handler>>();
  private readonly closeHandlers: Handler[] = [];
  private readonly reconnectingHandlers: Handler[] = [];
  private readonly reconnectedHandlers: Handler[] = [];

  public on(methodName: string, handler: Handler): void {
    if (!this.handlers.has(methodName)) {
      this.handlers.set(methodName, new Set());
    }
    this.handlers.get(methodName)!.add(handler);
  }

  public off(methodName: string, handler?: Handler): void {
    if (!handler) {
      this.handlers.delete(methodName);
      return;
    }
    this.handlers.get(methodName)?.delete(handler);
  }

  public async invoke(methodName: string, ...args: unknown[]): Promise<unknown> {
    this.invokeCalls.push({ method: methodName, args });
    return this.invokeImpl(methodName, ...args);
  }

  public async start(): Promise<void> {
    this.state = HubConnectionState.Connected;
  }

  public async stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
    for (const handler of this.closeHandlers) handler();
  }

  public onclose(handler: Handler): void {
    this.closeHandlers.push(handler);
  }

  public onreconnecting(handler: Handler): void {
    this.reconnectingHandlers.push(handler);
  }

  public onreconnected(handler: Handler): void {
    this.reconnectedHandlers.push(handler);
  }

  /** Test helper: fires a server->client event as SignalR would deliver it. */
  public emit(methodName: string, ...args: unknown[]): void {
    for (const handler of this.handlers.get(methodName) ?? []) {
      handler(...args);
    }
  }

  /** Test helper: simulates a drop + automatic-reconnect cycle. */
  public simulateReconnecting(): void {
    this.state = HubConnectionState.Reconnecting;
    for (const handler of this.reconnectingHandlers) handler();
  }

  /** Test helper: simulates the connection coming back and fires `onreconnected` handlers. */
  public async simulateReconnected(): Promise<void> {
    this.state = HubConnectionState.Connected;
    for (const handler of this.reconnectedHandlers) {
      await handler();
    }
  }

  public asHubConnection(): HubConnection {
    return this as unknown as HubConnection;
  }
}
