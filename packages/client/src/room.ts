import type { DotwireClient } from './client.js';
import {
  isSystemSender,
  type GetHistoryOptions,
  type HistoryPage,
  type MembershipRevoked,
  type MessageRetracted,
  type PresenceDelta,
  type RoomMessage,
  type SendAck,
  type SubscribeOptions,
  type TypingNotification
} from './types.js';

export class DotwireRoomHandle {
  private autoStopTypingTimer: ReturnType<typeof setTimeout> | null = null;
  private lastTypingSentAt = 0;
  private isCurrentlyTyping = false;
  private readonly listeners: (() => void)[] = [];

  constructor(
    public readonly roomId: string,
    private readonly client: DotwireClient
  ) {}

  /**
   * Subscribes on the hub and, when `history` is given, fetches and delivers that many of the
   * latest messages (spec §6) before returning them.
   */
  public async subscribe(options: SubscribeOptions = {}): Promise<HistoryPage | undefined> {
    await this.client.subscribe(this.roomId);

    if (options.history === undefined) {
      return undefined;
    }

    const page = await this.client.getHistory(this.roomId, { limit: options.history });
    for (const message of page.messages) {
      this.client.deliverMessage(message);
    }
    return page;
  }

  public async unsubscribe(): Promise<void> {
    this.clearTypingTimer();
    return this.client.unsubscribe(this.roomId);
  }

  public async sendMessage(content: string, timeoutMs?: number): Promise<SendAck> {
    return this.client.sendMessage(this.roomId, content, timeoutMs);
  }

  public async getHistory(options: GetHistoryOptions = {}): Promise<HistoryPage> {
    return this.client.getHistory(this.roomId, options);
  }

  public async getParticipants(timeoutMs?: number): Promise<string[]> {
    return this.client.getParticipants(this.roomId, timeoutMs);
  }

  public async sendTyping(isTyping: boolean): Promise<void> {
    this.clearTypingTimer();
    this.isCurrentlyTyping = isTyping;
    if (isTyping) {
      this.lastTypingSentAt = Date.now();
      const autoStopMs = this.client.options.autoStopTypingMs ?? 3000;
      this.autoStopTypingTimer = setTimeout(() => {
        this.stopTyping().catch(() => {});
      }, autoStopMs);
    }
    return this.client.sendTyping(this.roomId, isTyping);
  }

  public async startTyping(): Promise<void> {
    return this.sendTyping(true);
  }

  public async stopTyping(): Promise<void> {
    this.clearTypingTimer();
    if (this.isCurrentlyTyping) {
      this.isCurrentlyTyping = false;
      return this.client.sendTyping(this.roomId, false);
    }
  }

  public async notifyTyping(): Promise<void> {
    const now = Date.now();
    const throttleMs = this.client.options.typingThrottleMs ?? 1000;
    const autoStopMs = this.client.options.autoStopTypingMs ?? 3000;

    this.clearTypingTimer();
    this.autoStopTypingTimer = setTimeout(() => {
      this.stopTyping().catch(() => {});
    }, autoStopMs);

    if (!this.isCurrentlyTyping || now - this.lastTypingSentAt >= throttleMs) {
      this.isCurrentlyTyping = true;
      this.lastTypingSentAt = now;
      await this.client.sendTyping(this.roomId, true);
    }
  }

  private clearTypingTimer(): void {
    if (this.autoStopTypingTimer) {
      clearTimeout(this.autoStopTypingTimer);
      this.autoStopTypingTimer = null;
    }
  }

  public onMessage(handler: (message: RoomMessage) => void): () => void {
    const unsub = this.client.onMessage((message) => {
      if (message.roomId === this.roomId) {
        handler(message);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public onSystemMessage(handler: (message: RoomMessage) => void): () => void {
    const unsub = this.client.onMessage((message) => {
      if (message.roomId === this.roomId && isSystemSender(message.senderId)) {
        handler(message);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public onUserMessage(handler: (message: RoomMessage) => void): () => void {
    const unsub = this.client.onMessage((message) => {
      if (message.roomId === this.roomId && !isSystemSender(message.senderId)) {
        handler(message);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public onMessageRetracted(handler: (payload: MessageRetracted) => void): () => void {
    const unsub = this.client.onMessageRetracted((payload) => {
      if (payload.roomId === this.roomId) {
        handler(payload);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public onMembershipRevoked(handler: (payload: MembershipRevoked) => void): () => void {
    const unsub = this.client.onMembershipRevoked((payload) => {
      if (payload.roomId === this.roomId) {
        handler(payload);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public onTyping(handler: (notification: TypingNotification) => void): () => void {
    const unsub = this.client.onTyping((notification) => {
      if (notification.roomId === this.roomId) {
        handler(notification);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public onTypingWithExpiry(
    handler: (notification: TypingNotification) => void,
    autoExpireMs = 3500
  ): () => void {
    const expiryTimers = new Map<string, ReturnType<typeof setTimeout>>();

    const unsub = this.client.onTyping((notification) => {
      if (notification.roomId !== this.roomId) return;

      const existing = expiryTimers.get(notification.userId);
      if (existing) {
        clearTimeout(existing);
        expiryTimers.delete(notification.userId);
      }

      handler(notification);

      if (notification.isTyping) {
        const timer = setTimeout(() => {
          expiryTimers.delete(notification.userId);
          handler({
            roomId: this.roomId,
            userId: notification.userId,
            isTyping: false
          });
        }, autoExpireMs);
        expiryTimers.set(notification.userId, timer);
      }
    });

    const cleanup = () => {
      unsub();
      for (const timer of expiryTimers.values()) {
        clearTimeout(timer);
      }
      expiryTimers.clear();
    };

    this.listeners.push(cleanup);
    return cleanup;
  }

  public onPresence(handler: (delta: PresenceDelta) => void): () => void {
    const unsub = this.client.onPresence((delta) => {
      if (delta.roomId === this.roomId) {
        handler(delta);
      }
    });
    this.listeners.push(unsub);
    return unsub;
  }

  public dispose(): void {
    this.clearTypingTimer();
    for (const cleanup of this.listeners) {
      cleanup();
    }
    this.listeners.length = 0;
    this.unsubscribe().catch(() => {});
  }
}
