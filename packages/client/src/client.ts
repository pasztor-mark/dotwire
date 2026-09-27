import {
  HttpTransportType,
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  IHttpConnectionOptions,
  LogLevel
} from '@microsoft/signalr';
import { toDotwireClientError } from './errors.js';
import { BoundedSeqSet } from './seq-set.js';
import { DotwireRoomHandle } from './room.js';
import { TokenManager } from './token.js';
import {
  ConnectionState,
  DotwireClientOptions,
  GetHistoryOptions,
  HistoryPage,
  MembershipRevoked,
  MessageRetracted,
  PresenceDelta,
  RoomMessage,
  SendAck,
  TypingNotification,
  isSystemSender
} from './types.js';

interface WireHistoryMessage {
  seq: number;
  time: string;
  senderId: string;
  content: string;
}

interface WireHistoryResponse {
  roomId: string;
  messages: WireHistoryMessage[];
  hasMore: boolean;
}

interface RoomState {
  subscribed: boolean;
  lastSeq: number | null;
  seen: BoundedSeqSet;
}

export class DotwireClient {
  private readonly baseUrl: string;
  private readonly hubPath: string;
  private readonly connection: HubConnection;
  private readonly getToken: () => string | Promise<string>;
  private readonly messageListeners = new Set<(message: RoomMessage) => void>();
  private readonly retractedListeners = new Set<(payload: MessageRetracted) => void>();
  private readonly revokedListeners = new Set<(payload: MembershipRevoked) => void>();
  private readonly typingListeners = new Set<(notification: TypingNotification) => void>();
  private readonly presenceListeners = new Set<(delta: PresenceDelta) => void>();
  private readonly stateListeners = new Set<(state: ConnectionState) => void>();
  private readonly subscribedRooms = new Set<string>();
  private readonly roomState = new Map<string, RoomState>();
  private readonly activeRoomHandles = new Set<DotwireRoomHandle>();
  private unloadListener: (() => void) | null = null;

  constructor(public readonly options: DotwireClientOptions) {
    if (!options.tokenUrl && !options.getAccessToken) {
      throw new Error('DotwireClient requires either "tokenUrl" or "getAccessToken".');
    }

    this.baseUrl = options.baseUrl.replace(/\/$/, '');
    this.hubPath = options.hubPath ?? '/hub/rooms';

    if (options.getAccessToken) {
      this.getToken = options.getAccessToken;
    } else {
      const tokenManager = new TokenManager(options.tokenUrl!, options.tokenRefreshMarginMs ?? 30000);
      this.getToken = () => tokenManager.getToken();
    }

    this.connection = this.buildConnection();
    this.registerHubHandlers();
    this.registerUnloadHandler();
  }

  private buildConnection(): HubConnection {
    const url = `${this.baseUrl}${this.hubPath}`;
    const httpOptions: IHttpConnectionOptions = {
      accessTokenFactory: () => this.getToken()
    };

    if ((this.options.transport ?? 'websockets') === 'websockets') {
      httpOptions.transport = HttpTransportType.WebSockets;
      httpOptions.skipNegotiation = true;
    }

    if (this.options.connectionFactory) {
      return this.options.connectionFactory(url, httpOptions);
    }

    const builder = new HubConnectionBuilder()
      .withUrl(url, httpOptions)
      .configureLogging(mapLogLevel(this.options.logLevel));

    if (this.options.automaticReconnect !== false) {
      builder.withAutomaticReconnect();
    }

    const connection = builder.build();
    connection.serverTimeoutInMilliseconds = this.options.serverTimeoutMs ?? 90000;
    connection.keepAliveIntervalInMilliseconds = this.options.keepAliveIntervalMs ?? 15000;
    return connection;
  }

  private registerHubHandlers(): void {
    this.connection.on('ReceiveMessage', (message: RoomMessage) => {
      this.deliverMessage(message);
    });

    this.connection.on('UserTyping', (notification: TypingNotification) => {
      for (const listener of this.typingListeners) {
        listener(notification);
      }
    });

    this.connection.on('PresenceUpdated', (delta: PresenceDelta) => {
      for (const listener of this.presenceListeners) {
        listener(delta);
      }
    });

    this.connection.on('MessageRetracted', (payload: MessageRetracted) => {
      for (const listener of this.retractedListeners) {
        listener(payload);
      }
    });

    this.connection.on('MembershipRevoked', (payload: MembershipRevoked) => {
      this.subscribedRooms.delete(payload.roomId);
      const state = this.roomState.get(payload.roomId);
      if (state) state.subscribed = false;
      for (const listener of this.revokedListeners) {
        listener(payload);
      }
    });

    this.connection.onclose(() => {
      this.notifyStateChange('disconnected');
    });

    this.connection.onreconnecting(() => {
      this.notifyStateChange('reconnecting');
    });

    this.connection.onreconnected(async () => {
      this.notifyStateChange('connected');
      for (const roomId of Array.from(this.subscribedRooms)) {
        try {
          await this.connection.invoke('Subscribe', roomId);
        } catch {
          continue;
        }
        if (this.options.gapFillOnReconnect === false) continue;
        try {
          await this.gapFill(roomId);
        } catch {
          // Best-effort: a failed gap-fill leaves lastSeq as-is; a later reconnect retries.
        }
      }
    });
  }

  private registerUnloadHandler(): void {
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
      const onUnload = () => {
        this.disconnect().catch(() => {});
      };
      window.addEventListener('beforeunload', onUnload);
      window.addEventListener('pagehide', onUnload);
      this.unloadListener = () => {
        window.removeEventListener('beforeunload', onUnload);
        window.removeEventListener('pagehide', onUnload);
      };
    }
  }

  private notifyStateChange(state: ConnectionState): void {
    for (const listener of this.stateListeners) {
      listener(state);
    }
  }

  private getOrCreateRoomState(roomId: string): RoomState {
    let state = this.roomState.get(roomId);
    if (!state) {
      state = { subscribed: false, lastSeq: null, seen: new BoundedSeqSet(1000) };
      this.roomState.set(roomId, state);
    }
    return state;
  }

  /**
   * Applies seq dedupe/tracking (spec §6) and, if the message is new, notifies listeners.
   * Used for both live `ReceiveMessage` delivery and history/gap-fill replay.
   */
  public deliverMessage(message: RoomMessage): void {
    const state = this.getOrCreateRoomState(message.roomId);
    if (state.seen.has(message.seq)) return;
    state.seen.add(message.seq);
    if (state.lastSeq === null || message.seq > state.lastSeq) {
      state.lastSeq = message.seq;
    }
    for (const listener of this.messageListeners) {
      listener(message);
    }
  }

  private async gapFill(roomId: string): Promise<void> {
    const state = this.roomState.get(roomId);
    if (!state || state.lastSeq === null) return;

    const pageSize = this.options.gapFillPageSize ?? 100;
    let hasMore = true;

    while (hasMore) {
      const afterSeq = state.lastSeq ?? undefined;
      const page = await this.getHistory(roomId, { afterSeq, limit: pageSize });
      if (page.messages.length === 0) break;

      for (const message of page.messages) {
        this.deliverMessage({ ...message, replayed: true });
      }

      hasMore = page.hasMore;
    }
  }

  public getConnectionState(): ConnectionState {
    switch (this.connection.state) {
      case HubConnectionState.Connected:
        return 'connected';
      case HubConnectionState.Connecting:
        return 'connecting';
      case HubConnectionState.Reconnecting:
        return 'reconnecting';
      case HubConnectionState.Disconnecting:
        return 'disconnecting';
      default:
        return 'disconnected';
    }
  }

  public async connect(): Promise<void> {
    if (this.connection.state === HubConnectionState.Connected) {
      return;
    }
    this.notifyStateChange('connecting');
    await this.connection.start();
    this.notifyStateChange('connected');
  }

  public async disconnect(): Promise<void> {
    if (this.connection.state === HubConnectionState.Disconnected) {
      return;
    }
    this.notifyStateChange('disconnecting');
    await this.connection.stop();
    this.subscribedRooms.clear();
    this.notifyStateChange('disconnected');
  }

  public room(roomId: string): DotwireRoomHandle {
    const handle = new DotwireRoomHandle(roomId, this);
    this.activeRoomHandles.add(handle);
    return handle;
  }

  public async subscribe(roomId: string): Promise<void> {
    if (this.connection.state !== HubConnectionState.Connected) {
      await this.connect();
    }
    await this.connection.invoke('Subscribe', roomId);
    this.subscribedRooms.add(roomId);
    this.getOrCreateRoomState(roomId).subscribed = true;
  }

  public async unsubscribe(roomId: string): Promise<void> {
    if (this.connection.state === HubConnectionState.Connected) {
      await this.connection.invoke('Unsubscribe', roomId);
    }
    this.subscribedRooms.delete(roomId);
    const state = this.roomState.get(roomId);
    if (state) state.subscribed = false;
  }

  public async sendTyping(roomId: string, isTyping: boolean): Promise<void> {
    if (this.connection.state !== HubConnectionState.Connected) {
      return;
    }
    await this.connection.invoke('Typing', roomId, isTyping);
  }

  public async startTyping(roomId: string): Promise<void> {
    return this.sendTyping(roomId, true);
  }

  public async stopTyping(roomId: string): Promise<void> {
    return this.sendTyping(roomId, false);
  }

  public async sendMessage(roomId: string, content: string, timeoutMs?: number): Promise<SendAck> {
    const timeout = timeoutMs ?? this.options.requestTimeoutMs ?? 15000;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);

    try {
      let response: Response;
      if (this.options.sendUrl) {
        response = await fetch(this.options.sendUrl, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify({ roomId, content }),
          signal: controller.signal
        });
      } else {
        const token = await this.getToken();
        response = await fetch(`${this.baseUrl}/rooms/${roomId}/messages`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${token}`
          },
          body: JSON.stringify({ content }),
          signal: controller.signal
        });
      }

      if (!response.ok) {
        throw await toDotwireClientError(response);
      }

      return (await response.json()) as SendAck;
    } finally {
      clearTimeout(timer);
    }
  }

  public async getHistory(roomId: string, options: GetHistoryOptions = {}): Promise<HistoryPage> {
    const token = await this.getToken();
    const params = new URLSearchParams();
    if (options.afterSeq !== undefined) params.set('afterSeq', options.afterSeq.toString());
    if (options.beforeSeq !== undefined) params.set('beforeSeq', options.beforeSeq.toString());
    if (options.limit !== undefined) params.set('limit', options.limit.toString());

    const qs = params.toString();
    const url = `${this.baseUrl}/rooms/${roomId}/messages${qs ? `?${qs}` : ''}`;
    const timeout = options.timeoutMs ?? this.options.requestTimeoutMs ?? 15000;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);

    try {
      const response = await fetch(url, {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${token}`
        },
        signal: controller.signal
      });

      if (!response.ok) {
        throw await toDotwireClientError(response);
      }

      const body = (await response.json()) as WireHistoryResponse;
      const messages: RoomMessage[] = body.messages.map((m) => ({
        roomId: body.roomId,
        seq: m.seq,
        senderId: m.senderId,
        time: m.time,
        content: m.content
      }));

      return { messages, hasMore: body.hasMore };
    } finally {
      clearTimeout(timer);
    }
  }

  public async getParticipants(roomId: string, timeoutMs?: number): Promise<string[]> {
    const token = await this.getToken();
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs ?? this.options.requestTimeoutMs ?? 15000);
    try {
      const response = await fetch(`${this.baseUrl}/rooms/${roomId}/participants`, {
        method: 'GET',
        headers: { Authorization: `Bearer ${token}` },
        signal: controller.signal
      });
      if (!response.ok) throw await toDotwireClientError(response);
      return (await response.json()) as string[];
    } finally {
      clearTimeout(timer);
    }
  }

  public onMessage(handler: (message: RoomMessage) => void): () => void {
    this.messageListeners.add(handler);
    return () => {
      this.messageListeners.delete(handler);
    };
  }

  public onSystemMessage(handler: (message: RoomMessage) => void): () => void {
    return this.onMessage((message) => {
      if (isSystemSender(message.senderId)) {
        handler(message);
      }
    });
  }

  public onUserMessage(handler: (message: RoomMessage) => void): () => void {
    return this.onMessage((message) => {
      if (!isSystemSender(message.senderId)) {
        handler(message);
      }
    });
  }

  public onMessageRetracted(handler: (payload: MessageRetracted) => void): () => void {
    this.retractedListeners.add(handler);
    return () => {
      this.retractedListeners.delete(handler);
    };
  }

  public onMembershipRevoked(handler: (payload: MembershipRevoked) => void): () => void {
    this.revokedListeners.add(handler);
    return () => {
      this.revokedListeners.delete(handler);
    };
  }

  public onTyping(handler: (notification: TypingNotification) => void): () => void {
    this.typingListeners.add(handler);
    return () => {
      this.typingListeners.delete(handler);
    };
  }

  public onPresence(handler: (delta: PresenceDelta) => void): () => void {
    this.presenceListeners.add(handler);
    return () => {
      this.presenceListeners.delete(handler);
    };
  }

  public onStateChange(handler: (state: ConnectionState) => void): () => void {
    this.stateListeners.add(handler);
    return () => {
      this.stateListeners.delete(handler);
    };
  }

  public dispose(): void {
    if (this.unloadListener) {
      this.unloadListener();
      this.unloadListener = null;
    }
    for (const room of this.activeRoomHandles) {
      room.dispose();
    }
    this.activeRoomHandles.clear();
    this.disconnect().catch(() => {});
  }
}

function mapLogLevel(level?: DotwireClientOptions['logLevel']): LogLevel {
  switch (level) {
    case 'error':
      return LogLevel.Error;
    case 'warning':
      return LogLevel.Warning;
    case 'information':
      return LogLevel.Information;
    case 'debug':
      return LogLevel.Debug;
    case 'none':
    default:
      return LogLevel.None;
  }
}
