import { createHmac, timingSafeEqual } from 'node:crypto';
import { DotwireTokenSigner } from './signer.js';
import { DotwireHostRoom } from './room.js';
import { DotwireApiError, mapErrorKind, PresendRejectedError, PostSendError } from './errors.js';
import { parseSseStream, toRoomEvent } from './sse.js';
import { delay, isAbortError } from './util.js';
import type {
  AddRoomMembersOptions,
  AuditCheckpointsResponse,
  AuditPageResponse,
  AuditVerificationResponse,
  ClientSendResult,
  DotwireHostConfig,
  DotwireRole,
  GetMessagesOptions,
  MintTokenOptions,
  PostSendContext,
  PresendContext,
  RedactMessageResponse,
  RetentionResponse,
  RoomEvent,
  RoomHistoryResponse,
  RoomMembersResponse,
  SendMessageResult,
  SendSystemMessageOptions,
  StreamRoomEventsOptions,
  TokenResponseOptions,
  UserExport,
  UserRoleResponse,
  UserRoomsResponse,
  WebhookResult
} from './types.js';

const DEFAULT_ADMIN_USER_ID = 'host-admin';
const DEFAULT_AUDITOR_USER_ID = 'host-auditor';
const DEFAULT_AUDIENCE = 'dotwire';
const DEFAULT_TOKEN_TTL = '15m';
const DEFAULT_REQUEST_TIMEOUT_MS = 15_000;
const DEFAULT_ROLE_CACHE_TTL_MS = 300_000;

/** Re-mints a cached admin/auditor token when less than this much life remains. */
const TOKEN_REFRESH_MARGIN_SECONDS = 60;

interface RawFetchOptions {
  query?: Record<string, string | number | boolean | undefined>;
  body?: unknown;
  token?: string;
  extraHeaders?: Record<string, string>;
  signal?: AbortSignal;
  /** When false, no `AbortSignal.timeout` is applied - used for the long-lived SSE GET. */
  timeout?: boolean;
}

/** Lazily mints a token and re-mints it once fewer than a minute of life remains. */
class CachedToken {
  private readonly mint: () => Promise<string>;
  private token: string | undefined;
  private expiresAtSeconds = 0;

  constructor(mint: () => Promise<string>) {
    this.mint = mint;
  }

  async get(): Promise<string> {
    const now = Date.now() / 1000;
    if (this.token && this.expiresAtSeconds - now > TOKEN_REFRESH_MARGIN_SECONDS) {
      return this.token;
    }
    this.token = await this.mint();
    this.expiresAtSeconds = decodeExpiry(this.token) ?? now + 900;
    return this.token;
  }
}

function decodeExpiry(jwt: string): number | undefined {
  try {
    const payloadSegment = jwt.split('.')[1];
    const json = Buffer.from(payloadSegment, 'base64url').toString('utf8');
    const payload = JSON.parse(json) as { exp?: number };
    return typeof payload.exp === 'number' ? payload.exp : undefined;
  } catch {
    return undefined;
  }
}

/** Caches a resolved role per user id for `roleCacheTtlMs`. */
class RoleCache {
  private readonly entries = new Map<string, { role: DotwireRole; expiresAt: number }>();
  private readonly ttlMs: number;

  constructor(ttlMs: number) {
    this.ttlMs = ttlMs;
  }

  get(userId: string): DotwireRole | undefined {
    const entry = this.entries.get(userId);
    if (!entry) return undefined;
    if (Date.now() > entry.expiresAt) {
      this.entries.delete(userId);
      return undefined;
    }
    return entry.role;
  }

  set(userId: string, role: DotwireRole): void {
    this.entries.set(userId, { role, expiresAt: Date.now() + this.ttlMs });
  }
}

/**
 * Node/TypeScript counterpart of `Dotwire.Host.DotwireHostClient` (.NET). Mints its own tokens
 * and talks to a dotwire server entirely over its frozen HTTP surface - see spec §3 for the
 * server's wire shapes and §4/§5 for the identity and role-resolution rules this class follows.
 */
export class DotwireHostClient {
  private readonly config: DotwireHostConfig;
  private readonly signer: DotwireTokenSigner;
  private readonly baseUrl: string;
  private readonly fetchImpl: typeof fetch;
  private readonly requestTimeoutMs: number;
  private readonly adminUserId: string;
  private readonly auditorUserId: string;
  private readonly defaultTtl: string;
  private readonly roleCache: RoleCache;
  private readonly adminToken: CachedToken;
  private readonly auditorToken: CachedToken;

  presend?: (ctx: PresendContext) => Promise<import('./types.js').PresendResult> | import('./types.js').PresendResult;
  postSend?: (ctx: PostSendContext) => Promise<void> | void;

  constructor(config: DotwireHostConfig) {
    this.config = config;
    this.signer = new DotwireTokenSigner({
      ...config,
      audience: config.audience ?? DEFAULT_AUDIENCE,
      defaultTokenTtl: config.defaultTokenTtl ?? DEFAULT_TOKEN_TTL
    });
    this.baseUrl = config.baseUrl.replace(/\/$/, '');
    this.fetchImpl = config.fetch ?? fetch;
    this.requestTimeoutMs = config.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
    this.adminUserId = config.adminUserId ?? DEFAULT_ADMIN_USER_ID;
    this.auditorUserId = config.auditorUserId ?? DEFAULT_AUDITOR_USER_ID;
    this.defaultTtl = config.defaultTokenTtl ?? DEFAULT_TOKEN_TTL;
    this.roleCache = new RoleCache(config.roleCacheTtlMs ?? DEFAULT_ROLE_CACHE_TTL_MS);
    this.adminToken = new CachedToken(() => this.signer.mintToken(this.adminUserId, { role: 'admin', ttl: this.defaultTtl }));
    this.auditorToken = new CachedToken(() => this.signer.mintToken(this.auditorUserId, { role: 'auditor', ttl: this.defaultTtl }));
    this.presend = config.presend;
    this.postSend = config.postSend;
  }

  // ---------------------------------------------------------------------
  // Tokens
  // ---------------------------------------------------------------------

  /** Mints a token directly. An explicit `role` bypasses all role resolution. */
  async mintToken(userId: string, options: MintTokenOptions = {}): Promise<string> {
    return this.signer.mintToken(userId, { ttl: this.defaultTtl, ...options });
  }

  /**
   * Returns a `Response` with body `{ token }`. When `role` is omitted, resolves it through the
   * cached admin lookup and falls back to `member` for an unknown user (404 on the lookup) since
   * there is no request of its own to fail here.
   */
  async tokenResponse(options: TokenResponseOptions): Promise<Response> {
    const role = options.role ?? (await this.resolveRoleForToken(options.userId));
    const token = await this.mintToken(options.userId, { role, displayName: options.displayName, ttl: options.ttl });
    return jsonResponse(200, { token });
  }

  private async resolveRoleForToken(userId: string): Promise<DotwireRole> {
    const cached = this.roleCache.get(userId);
    if (cached !== undefined) return cached;
    const looked = await this.lookupRole(userId);
    const role = looked ?? 'member';
    this.roleCache.set(userId, role);
    return role;
  }

  // ---------------------------------------------------------------------
  // Roles
  // ---------------------------------------------------------------------

  async setUserRole(userId: string, role: DotwireRole): Promise<UserRoleResponse> {
    const res = await this.adminRequest('PUT', `/admin/users/${encodeURIComponent(userId)}/role`, { body: { role } });
    await this.throwIfError(res, 'PUT', `/admin/users/${userId}/role`);
    this.roleCache.set(userId, role);
    return (await res.json()) as UserRoleResponse;
  }

  async getUserRole(userId: string): Promise<UserRoleResponse | null> {
    const path = `/admin/users/${userId}/role`;
    const res = await this.adminRequest('GET', `/admin/users/${encodeURIComponent(userId)}/role`);
    if (res.status === 404) return null;
    await this.throwIfError(res, 'GET', path);
    return (await res.json()) as UserRoleResponse;
  }

  async deleteUserRole(userId: string): Promise<void> {
    const path = `/admin/users/${userId}/role`;
    const res = await this.adminRequest('DELETE', `/admin/users/${encodeURIComponent(userId)}/role`);
    if (res.status === 404) return;
    await this.throwIfError(res, 'DELETE', path);
  }

  async getUserRooms(userId: string): Promise<string[]> {
    const path = `/admin/users/${userId}/rooms`;
    const res = await this.adminRequest('GET', `/admin/users/${encodeURIComponent(userId)}/rooms`);
    await this.throwIfError(res, 'GET', path);
    const data = (await res.json()) as UserRoomsResponse;
    return data.roomIds;
  }

  async exportUser(userId: string): Promise<UserExport> {
    const path = `/admin/users/${userId}/export`;
    const res = await this.adminRequest('GET', `/admin/users/${encodeURIComponent(userId)}/export`);
    await this.throwIfError(res, 'GET', path);
    return (await res.json()) as UserExport;
  }

  /** Returns the raw response stream so the caller can pipe an arbitrarily large export without buffering it. */
  async exportUserStream(userId: string): Promise<ReadableStream<Uint8Array>> {
    const path = `/admin/users/${userId}/export`;
    const res = await this.adminRequest('GET', `/admin/users/${encodeURIComponent(userId)}/export`);
    await this.throwIfError(res, 'GET', path);
    if (!res.body) throw new Error('dotwire export response had no body');
    return res.body;
  }

  private async lookupRole(userId: string): Promise<DotwireRole | null> {
    const res = await this.adminRequest('GET', `/admin/users/${encodeURIComponent(userId)}/role`);
    if (res.status === 404) return null;
    await this.throwIfError(res, 'GET', `/admin/users/${userId}/role`);
    const data = (await res.json()) as UserRoleResponse;
    return data.role;
  }

  // ---------------------------------------------------------------------
  // Rooms
  // ---------------------------------------------------------------------

  room(roomId: string): DotwireHostRoom {
    return new DotwireHostRoom(this, roomId);
  }

  async addRoomMembers(roomId: string, userIds: string[], options: AddRoomMembersOptions = {}): Promise<string[]> {
    const ensureMemberRole = options.ensureMemberRole ?? true;
    const path = `/admin/rooms/${roomId}/members`;
    const res = await this.adminRequest('POST', path, { body: { userIds, ensureMemberRole } });
    await this.throwIfError(res, 'POST', path);
    const data = (await res.json()) as RoomMembersResponse;
    return data.members;
  }

  async removeRoomMember(roomId: string, userId: string): Promise<void> {
    const path = `/admin/rooms/${roomId}/members/${userId}`;
    const res = await this.adminRequest('DELETE', `/admin/rooms/${roomId}/members/${encodeURIComponent(userId)}`);
    if (res.status === 404) return;
    await this.throwIfError(res, 'DELETE', path);
  }

  async getRoomMembers(roomId: string): Promise<string[]> {
    const path = `/admin/rooms/${roomId}/members`;
    const res = await this.adminRequest('GET', path);
    await this.throwIfError(res, 'GET', path);
    const data = (await res.json()) as RoomMembersResponse;
    return data.members;
  }

  /** Reads participants under the user's own room membership, without admin access. */
  async getRoomParticipants(roomId: string, asUserId: string): Promise<string[]> {
    const path = `/rooms/${roomId}/participants`;
    const res = await this.fetchAsUser('GET', path, asUserId);
    await this.throwIfError(res, 'GET', path);
    return (await res.json()) as string[];
  }

  // ---------------------------------------------------------------------
  // Sending
  // ---------------------------------------------------------------------

  async sendAsUser(roomId: string, userId: string, content: string): Promise<SendMessageResult> {
    if (this.presend) {
      const result = await this.presend({ roomId, content, senderUserId: userId });
      if (!result.allow) {
        throw new PresendRejectedError(result.rejectionReason ?? 'Message rejected by presend hook.');
      }
      if (result.content !== undefined) {
        content = result.content;
      }
    }

    const path = `/rooms/${roomId}/messages`;
    const res = await this.fetchAsUser('POST', `/rooms/${roomId}/messages`, userId, { body: { content } });
    await this.throwIfError(res, 'POST', path);
    const ack = (await res.json()) as SendMessageResult;

    if (this.postSend) {
      try {
        await this.postSend({ roomId, seq: ack.seq, time: ack.time, content, senderUserId: userId });
      } catch (err) {
        throw new PostSendError(ack, err);
      }
    }

    return ack;
  }

  /** Admin message injection (spec §3.3). Presend/postSend hooks do not run for system injections. */
  async sendSystemMessage(roomId: string, content: string, options: SendSystemMessageOptions = {}): Promise<SendMessageResult> {
    const senderId = options.senderId ?? 'system';
    const path = `/admin/rooms/${roomId}/messages`;
    const res = await this.adminRequest('POST', path, { body: { content, senderId } });
    await this.throwIfError(res, 'POST', path);
    return (await res.json()) as SendMessageResult;
  }

  async getMessages(roomId: string, options: GetMessagesOptions): Promise<RoomHistoryResponse> {
    const path = `/rooms/${roomId}/messages`;
    const res = await this.fetchAsUser('GET', path, options.asUserId, {
      query: { afterSeq: options.afterSeq, beforeSeq: options.beforeSeq, limit: options.limit }
    });
    await this.throwIfError(res, 'GET', path);
    return (await res.json()) as RoomHistoryResponse;
  }

  /** Parses SSE from `GET /rooms/{roomId}/events`, reconnecting on drop when `autoResume` (default true). */
  streamRoomEvents(roomId: string, options: StreamRoomEventsOptions): AsyncIterable<RoomEvent> {
    return this.streamRoomEventsGenerator(roomId, options);
  }

  private async *streamRoomEventsGenerator(roomId: string, options: StreamRoomEventsOptions): AsyncGenerator<RoomEvent> {
    const autoResume = options.autoResume ?? true;
    const signal = options.signal;
    let lastSeq = options.afterSeq;
    let backoffMs = 1000;
    const maxBackoffMs = 30_000;

    while (true) {
      if (signal?.aborted) return;

      let response: Response;
      try {
        response = await this.fetchAsUser('GET', `/rooms/${roomId}/events`, options.asUserId, {
          query: lastSeq === undefined ? {} : { afterSeq: lastSeq },
          extraHeaders: {
            Accept: 'text/event-stream',
            ...(lastSeq !== undefined ? { 'Last-Event-ID': String(lastSeq) } : {})
          },
          signal,
          timeout: false
        });
      } catch (err) {
        if (signal?.aborted || isAbortError(err)) return;
        if (!autoResume) throw err;
        await delay(backoffMs, signal).catch(() => undefined);
        backoffMs = Math.min(backoffMs * 2, maxBackoffMs);
        continue;
      }

      if (!response.ok) {
        await this.throwIfError(response, 'GET', `/rooms/${roomId}/events`);
      }
      if (!response.body) return;

      let revoked = false;
      try {
        for await (const raw of parseSseStream(response.body, signal)) {
          const event = toRoomEvent(raw);
          if (!event) continue;
          if (event.type === 'message') lastSeq = event.seq;
          yield event;
          if (event.type === 'revoked') {
            revoked = true;
            break;
          }
        }
      } catch (err) {
        if (signal?.aborted || isAbortError(err)) return;
        if (!autoResume) throw err;
        await delay(backoffMs, signal).catch(() => undefined);
        backoffMs = Math.min(backoffMs * 2, maxBackoffMs);
        continue;
      }

      if (revoked || !autoResume || signal?.aborted) return;

      backoffMs = 1000; // clean disconnect after a successful connection: reset backoff before reconnecting
      await delay(backoffMs, signal).catch(() => undefined);
    }
  }

  async redactMessage(roomId: string, seq: number): Promise<RedactMessageResponse> {
    const path = `/admin/rooms/${roomId}/messages/${seq}`;
    const res = await this.adminRequest('DELETE', path);
    await this.throwIfError(res, 'DELETE', path);
    return (await res.json()) as RedactMessageResponse;
  }

  // ---------------------------------------------------------------------
  // Audit
  // ---------------------------------------------------------------------

  async readAuditLog(afterId = 0, limit = 100): Promise<AuditPageResponse> {
    const path = '/audit';
    const res = await this.auditorRequest('GET', path, { query: { afterId, limit } });
    await this.throwIfError(res, 'GET', path);
    return (await res.json()) as AuditPageResponse;
  }

  async verifyAuditLog(full = false): Promise<AuditVerificationResponse> {
    const path = '/audit/verify';
    const res = await this.auditorRequest('GET', path, { query: { full } });
    await this.throwIfError(res, 'GET', path);
    return (await res.json()) as AuditVerificationResponse;
  }

  async getAuditCheckpoints(limit = 30): Promise<AuditCheckpointsResponse['checkpoints']> {
    const path = '/audit/checkpoints';
    const res = await this.auditorRequest('GET', path, { query: { limit } });
    await this.throwIfError(res, 'GET', path);
    const data = (await res.json()) as AuditCheckpointsResponse;
    return data.checkpoints;
  }

  // ---------------------------------------------------------------------
  // Retention
  // ---------------------------------------------------------------------

  async getMessageRetentionDays(): Promise<number> {
    const path = '/admin/retention';
    const res = await this.adminRequest('GET', path);
    await this.throwIfError(res, 'GET', path);
    return ((await res.json()) as RetentionResponse).messagesDays;
  }

  async setMessageRetentionDays(days: number): Promise<number> {
    const path = '/admin/retention';
    const res = await this.adminRequest('PUT', path, { body: { messagesDays: days } });
    await this.throwIfError(res, 'PUT', path);
    return ((await res.json()) as RetentionResponse).messagesDays;
  }

  // ---------------------------------------------------------------------
  // Request handlers (web-standard Request/Response)
  // ---------------------------------------------------------------------

  /** Parses `{ roomId, content }`, calls {@link sendAsUser}, and mirrors the server's own send response shape/status. */
  async handleClientSend(request: Request, senderUserId: string): Promise<Response> {
    let parsed: unknown;
    try {
      parsed = await request.json();
    } catch {
      return jsonResponse(400, { error: 'invalid_request' });
    }
    const result = await this.handleClientSendCore(parsed, senderUserId);
    return jsonResponse(result.status, result.body);
  }

  /** Express-friendly variant: `body` is already-parsed JSON. */
  async handleClientSendBody(body: unknown, senderUserId: string): Promise<ClientSendResult> {
    return this.handleClientSendCore(body, senderUserId);
  }

  private async handleClientSendCore(body: unknown, senderUserId: string): Promise<ClientSendResult> {
    const b = (body ?? {}) as { roomId?: unknown; content?: unknown };
    if (typeof b.roomId !== 'string' || typeof b.content !== 'string') {
      return { status: 400, body: { error: 'invalid_request' } };
    }
    try {
      const ack = await this.sendAsUser(b.roomId, senderUserId, b.content);
      return { status: 202, body: ack };
    } catch (err) {
      if (err instanceof PresendRejectedError) {
        return { status: 422, body: { error: 'presend_rejected', reason: err.reason } };
      }
      if (err instanceof DotwireApiError) {
        return { status: err.status, body: { error: err.code ?? 'unknown', reason: err.reason } };
      }
      throw err;
    }
  }

  /** Verifies `X-Dotwire-Signature` (when `webhookSecret` is set), runs the local `presend` hook, and mirrors the server's presend webhook response shape. */
  async handlePresendWebhook(request: Request): Promise<Response> {
    const text = await request.text();
    const signature = request.headers.get('x-dotwire-signature');
    const result = await this.handlePresendWebhookCore(text, signature);
    return jsonResponse(result.status, result.body);
  }

  async handlePresendWebhookBody(body: unknown, signatureHeader?: string | null, rawBody?: string): Promise<WebhookResult> {
    const text = rawBody ?? JSON.stringify(body);
    return this.handlePresendWebhookCore(text, signatureHeader ?? null, body);
  }

  private async handlePresendWebhookCore(rawText: string, signatureHeader: string | null, parsedBody?: unknown): Promise<WebhookResult> {
    if (!this.verifySignature(rawText, signatureHeader)) {
      return { status: 401, body: { allow: false, reason: 'invalid_signature' } };
    }
    let payload: { roomId?: unknown; senderId?: unknown; content?: unknown };
    try {
      payload = (parsedBody ?? JSON.parse(rawText)) as typeof payload;
    } catch {
      return { status: 400, body: { allow: false, reason: 'invalid_request' } };
    }
    if (typeof payload.roomId !== 'string' || typeof payload.senderId !== 'string' || typeof payload.content !== 'string') {
      return { status: 400, body: { allow: false, reason: 'invalid_request' } };
    }

    if (!this.presend) {
      return { status: 200, body: { allow: true } };
    }

    try {
      const result = await this.presend({ roomId: payload.roomId, content: payload.content, senderUserId: payload.senderId });
      if (!result.allow) {
        return { status: 200, body: { allow: false, reason: result.rejectionReason } };
      }
      return { status: 200, body: { allow: true, content: result.content } };
    } catch (err) {
      return { status: 200, body: { allow: false, reason: err instanceof Error ? err.message : 'presend hook failed' } };
    }
  }

  /** Verifies the signature, runs the local `postSend` hook, and always answers `{}` (postsend is best-effort). */
  async handlePostSendWebhook(request: Request): Promise<Response> {
    const text = await request.text();
    const signature = request.headers.get('x-dotwire-signature');
    const result = await this.handlePostSendWebhookCore(text, signature);
    return jsonResponse(result.status, result.body);
  }

  async handlePostSendWebhookBody(body: unknown, signatureHeader?: string | null, rawBody?: string): Promise<WebhookResult> {
    const text = rawBody ?? JSON.stringify(body);
    return this.handlePostSendWebhookCore(text, signatureHeader ?? null, body);
  }

  private async handlePostSendWebhookCore(rawText: string, signatureHeader: string | null, parsedBody?: unknown): Promise<WebhookResult> {
    if (!this.verifySignature(rawText, signatureHeader)) {
      return { status: 401, body: {} };
    }
    let payload: { roomId?: unknown; seq?: unknown; senderId?: unknown; content?: unknown; time?: unknown };
    try {
      payload = (parsedBody ?? JSON.parse(rawText)) as typeof payload;
    } catch {
      return { status: 400, body: {} };
    }
    if (
      typeof payload.roomId !== 'string' ||
      typeof payload.seq !== 'number' ||
      typeof payload.senderId !== 'string' ||
      typeof payload.content !== 'string' ||
      typeof payload.time !== 'string'
    ) {
      return { status: 400, body: {} };
    }

    if (this.postSend) {
      try {
        await this.postSend({ roomId: payload.roomId, seq: payload.seq, time: payload.time, content: payload.content, senderUserId: payload.senderId });
      } catch {
        // Best-effort: postsend never affects the response the server sees.
      }
    }
    return { status: 200, body: {} };
  }

  private verifySignature(rawBody: string, signatureHeader: string | null): boolean {
    if (!this.config.webhookSecret) return true;
    if (!signatureHeader) return false;
    const prefix = 'sha256=';
    if (!signatureHeader.startsWith(prefix)) return false;
    const provided = signatureHeader.slice(prefix.length);
    const expected = createHmac('sha256', this.config.webhookSecret).update(rawBody, 'utf8').digest('hex');
    const providedBuf = Buffer.from(provided, 'hex');
    const expectedBuf = Buffer.from(expected, 'hex');
    if (providedBuf.length !== expectedBuf.length) return false;
    return timingSafeEqual(providedBuf, expectedBuf);
  }

  // ---------------------------------------------------------------------
  // HTTP plumbing
  // ---------------------------------------------------------------------

  private async adminRequest(method: string, path: string, opts: Omit<RawFetchOptions, 'token'> = {}): Promise<Response> {
    const token = await this.adminToken.get();
    return this.rawFetch(method, path, { ...opts, token });
  }

  private async auditorRequest(method: string, path: string, opts: Omit<RawFetchOptions, 'token'> = {}): Promise<Response> {
    const token = await this.auditorToken.get();
    return this.rawFetch(method, path, { ...opts, token });
  }

  /**
   * Role resolution for user-scoped operations (spec §4.3 / §5): try the cached role (or
   * `member` when nothing is cached); on 403, look the role up through the admin API, cache it,
   * and retry once. A second 403 surfaces as a `Forbidden` {@link DotwireApiError}.
   */
  private async fetchAsUser(method: string, path: string, userId: string, opts: Omit<RawFetchOptions, 'token'> = {}): Promise<Response> {
    const cachedRole = this.roleCache.get(userId);
    const firstRole = cachedRole ?? 'member';

    const attempt = async (role: DotwireRole): Promise<Response> => {
      const token = await this.signer.mintToken(userId, { role, ttl: this.defaultTtl });
      return this.rawFetch(method, path, { ...opts, token });
    };

    let res = await attempt(firstRole);
    if (res.status === 403) {
      const looked = await this.lookupRole(userId);
      const resolvedRole = looked ?? 'member';
      this.roleCache.set(userId, resolvedRole);
      res = await attempt(resolvedRole);
    }
    return res;
  }

  private async rawFetch(method: string, path: string, opts: RawFetchOptions): Promise<Response> {
    const url = new URL(this.baseUrl + path);
    if (opts.query) {
      for (const [key, value] of Object.entries(opts.query)) {
        if (value !== undefined) url.searchParams.set(key, String(value));
      }
    }

    const headers: Record<string, string> = { ...opts.extraHeaders };
    if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
    if (opts.body !== undefined) headers['Content-Type'] = 'application/json';

    const useTimeout = opts.timeout ?? true;
    let signal = opts.signal;
    if (useTimeout) {
      const timeoutSignal = AbortSignal.timeout(this.requestTimeoutMs);
      signal = opts.signal ? AbortSignal.any([opts.signal, timeoutSignal]) : timeoutSignal;
    }

    return this.fetchImpl(url.toString(), {
      method,
      headers,
      body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
      signal
    });
  }

  private async throwIfError(res: Response, method: string, path: string): Promise<void> {
    if (res.ok) return;
    let code: string | undefined;
    let reason: string | undefined;
    try {
      const clone = res.clone();
      const body = (await clone.json()) as { error?: string; reason?: string };
      code = body?.error;
      reason = body?.reason;
    } catch {
      // bare error response (e.g. plain 401/403/404) - no body to parse.
    }
    const retryAfterHeader = res.headers.get('retry-after');
    const retryAfterMs = retryAfterHeader ? Number(retryAfterHeader) * 1000 : undefined;
    throw new DotwireApiError({
      status: res.status,
      kind: mapErrorKind(res.status, code),
      code,
      reason,
      retryAfterMs,
      method,
      path
    });
  }
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}
