import type { HubConnection, IHttpConnectionOptions } from '@microsoft/signalr';

export type ConnectionState =
  | 'disconnected'
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'disconnecting';

export interface DotwireClientOptions {
  baseUrl: string;
  /** GET, `credentials: 'include'`, expects `{ token }`. One of this or `getAccessToken` is required. */
  tokenUrl?: string;
  /** Escape hatch: caller owns caching/refresh. One of this or `tokenUrl` is required. */
  getAccessToken?: () => string | Promise<string>;
  /** Default 30000. */
  tokenRefreshMarginMs?: number;
  /** POST `{roomId, content}` with `credentials: 'include'`, no bearer header; a host route. */
  sendUrl?: string;
  /** Default `/hub/rooms`. */
  hubPath?: string;
  /** Default `'websockets'` (skipNegotiation). */
  transport?: 'websockets' | 'negotiate';
  /** Default 90000. */
  serverTimeoutMs?: number;
  /** Default 15000. */
  keepAliveIntervalMs?: number;
  /** Default true. */
  automaticReconnect?: boolean;
  /** Default true. */
  gapFillOnReconnect?: boolean;
  /** Default 100. */
  gapFillPageSize?: number;
  /** Default 15000. */
  requestTimeoutMs?: number;
  autoStopTypingMs?: number;
  typingThrottleMs?: number;
  logLevel?: 'none' | 'error' | 'warning' | 'information' | 'debug';
  /** Test seam for constructing the `HubConnection`. Defaults to the real signalr builder. */
  connectionFactory?: (url: string, options: IHttpConnectionOptions) => HubConnection;
}

/**
 * Delivered for both live messages (`ReceiveMessage`) and history pages; `replayed` is set
 * `true` only for reconnect gap-fill replay (spec §6, §12 - replaces `RoomMessageDelivery`).
 */
export interface RoomMessage {
  roomId: string;
  seq: number;
  senderId: string;
  time: string;
  content: string;
  replayed?: boolean;
}

export interface MessageRetracted {
  roomId: string;
  seq: number;
}

export interface MembershipRevoked {
  roomId: string;
  userId: string;
}

export interface TypingNotification {
  roomId: string;
  userId: string;
  isTyping: boolean;
}

export interface PresenceDelta {
  roomId: string;
  joined: string[];
  left: string[];
}

export interface SendAck {
  seq: number;
  time: string;
}

export interface HistoryPage {
  messages: RoomMessage[];
  hasMore: boolean;
}

export interface GetHistoryOptions {
  afterSeq?: number;
  beforeSeq?: number;
  limit?: number;
  timeoutMs?: number;
}

export interface SubscribeOptions {
  /** When set, fetches and delivers the latest page of history (as `getHistory({limit: history})`). */
  history?: number;
}

export function isSystemSender(senderId: string): boolean {
  if (!senderId) return false;
  const lower = senderId.toLowerCase();
  return lower === 'system' || lower.startsWith('system:') || lower.startsWith('bot:');
}
