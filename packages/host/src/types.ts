export type DotwireRole = 'member' | 'auditor' | 'admin';

export interface DotwireHostConfig {
  baseUrl: string;
  issuer: string;
  audience?: string;
  keyId: string;
  privateKeyPem: string;
  adminUserId?: string;
  auditorUserId?: string;
  /** jose-style duration string (e.g. "15m"), passed straight through to `SignJWT#setExpirationTime`. */
  defaultTokenTtl?: string;
  requestTimeoutMs?: number;
  /** HMAC-SHA256 secret used to verify `X-Dotwire-Signature` on incoming webhook calls. */
  webhookSecret?: string;
  roleCacheTtlMs?: number;
  /** Every request goes through this so tests can inject a fake. Defaults to global `fetch`. */
  fetch?: typeof fetch;
  /**
   * Runs in-process before {@link DotwireHostClient.sendAsUser} and {@link DotwireHostClient.handleClientSend},
   * and inside {@link DotwireHostClient.handlePresendWebhook} - one place for moderation logic that
   * covers both host-initiated sends and messages sent directly by `@dotwire/client`.
   */
  presend?: (ctx: PresendContext) => Promise<PresendResult> | PresendResult;
  /** Same idea as {@link presend}, run after a message is accepted; see {@link PostSendContext}. */
  postSend?: (ctx: PostSendContext) => Promise<void> | void;
}

export interface MintTokenOptions {
  role?: DotwireRole;
  displayName?: string;
  /** jose-style duration string, e.g. "15m". Defaults to `config.defaultTokenTtl`. */
  ttl?: string;
}

export interface PresendContext {
  roomId: string;
  content: string;
  senderUserId: string;
}

export interface PresendResult {
  allow: boolean;
  /** Rewritten content; only meaningful when `allow` is true. */
  content?: string;
  rejectionReason?: string;
}

export const PresendResult = {
  accept(content?: string): PresendResult {
    return { allow: true, content };
  },
  reject(reason: string): PresendResult {
    return { allow: false, rejectionReason: reason };
  }
};

/** What a postSend hook sees: the accepted message plus its ack, so it can be found again by (roomId, seq). */
export interface PostSendContext {
  roomId: string;
  seq: number;
  time: string;
  content: string;
  senderUserId: string;
}

export interface SendMessageResult {
  seq: number;
  time: string;
}

export interface HistoryMessage {
  seq: number;
  time: string;
  senderId: string;
  content: string;
}

export interface RoomHistoryResponse {
  roomId: string;
  messages: HistoryMessage[];
  hasMore: boolean;
}

export interface UserRoleResponse {
  userId: string;
  role: DotwireRole;
}

export interface RoomMembersResponse {
  roomId: string;
  members: string[];
}

export interface UserRoomsResponse {
  userId: string;
  roomIds: string[];
}

export interface RedactMessageResponse {
  roomId: string;
  seq: number;
  removedFromHistory: boolean;
  removedFromStream: boolean;
}

export interface RetentionResponse {
  messagesDays: number;
}

export interface AuditEntry {
  id: number;
  eventType: string;
  roomId: string | null;
  messageSeq: number | null;
  actorId: string | null;
  subjectId: string | null;
  value: number | null;
  time: string;
  hash: string;
}

export interface AuditPageResponse {
  events: AuditEntry[];
  hasMore: boolean;
}

export interface AuditVerificationResponse {
  ok: boolean;
  fromId: number;
  toId: number;
  checked: number;
  firstInvalidId: number | null;
  anchor: string | null;
}

export interface AuditCheckpointEntry {
  day: string;
  lastId: number;
  hash: string;
  createdAt: string;
}

export interface AuditCheckpointsResponse {
  checkpoints: AuditCheckpointEntry[];
}

export interface UserExportMessage {
  roomId: string;
  seq: number;
  time: string;
  content: string | null;
  undecryptable?: boolean;
}

export interface UserExportAuditEvent {
  id: number;
  eventType: string;
  roomId: string | null;
  messageSeq: number | null;
  actorId: string | null;
  subjectId: string | null;
  value: number | null;
  time: string;
}

export interface UserExport {
  userId: string;
  exportedAt: string;
  role: DotwireRole | null;
  roomIds: string[];
  messages: UserExportMessage[];
  auditEvents: UserExportAuditEvent[];
}

export interface GetMessagesOptions {
  asUserId: string;
  afterSeq?: number;
  beforeSeq?: number;
  limit?: number;
}

export interface AddRoomMembersOptions {
  /** Grants `member` to any listed user with no existing role, without touching an existing auditor/admin. Default true. */
  ensureMemberRole?: boolean;
}

export interface SendSystemMessageOptions {
  /** Defaults to "system". */
  senderId?: string;
}

export interface StreamRoomEventsOptions {
  asUserId: string;
  afterSeq?: number;
  /** Reconnect on drop with `Last-Event-ID`, exponential backoff 1s..30s, until cancelled or revoked. Default true. */
  autoResume?: boolean;
  signal?: AbortSignal;
}

export interface TokenResponseOptions {
  userId: string;
  role?: DotwireRole;
  displayName?: string;
  ttl?: string;
}

/** Discriminated union of everything `GET /rooms/{roomId}/events` can emit (spec §3.9). */
export type RoomEvent =
  | { type: 'message'; roomId: string; seq: number; senderId: string; time: string; content: string; replayed?: boolean }
  | { type: 'retracted'; roomId: string; seq: number }
  | { type: 'typing'; roomId: string; userId: string; isTyping: boolean }
  | { type: 'presence'; roomId: string; joined: string[]; left: string[] }
  | { type: 'revoked'; roomId: string };

/** Result of {@link DotwireHostClient.handleClientSendBody} / the Request-based `handleClientSend`. */
export interface ClientSendResult {
  status: number;
  body: { seq: number; time: string } | { error: string; reason?: string };
}

/** Result of the two webhook handlers' body-based variants. */
export interface WebhookResult {
  status: number;
  body: { allow: boolean; content?: string; reason?: string } | Record<string, never>;
}
