import type { DotwireHostClient } from './client.js';
import type {
  AddRoomMembersOptions,
  GetMessagesOptions,
  RedactMessageResponse,
  RoomEvent,
  RoomHistoryResponse,
  SendMessageResult,
  SendSystemMessageOptions,
  StreamRoomEventsOptions
} from './types.js';

/** Mirrors {@link DotwireHostClient}'s room-scoped methods without the repeated `roomId` argument. */
export class DotwireHostRoom {
  private readonly client: DotwireHostClient;
  readonly roomId: string;

  constructor(client: DotwireHostClient, roomId: string) {
    this.client = client;
    this.roomId = roomId;
  }

  addMembers(userIds: string[], options?: AddRoomMembersOptions): Promise<string[]> {
    return this.client.addRoomMembers(this.roomId, userIds, options);
  }

  removeMember(userId: string): Promise<void> {
    return this.client.removeRoomMember(this.roomId, userId);
  }

  getMembers(): Promise<string[]> {
    return this.client.getRoomMembers(this.roomId);
  }

  getParticipants(asUserId: string): Promise<string[]> {
    return this.client.getRoomParticipants(this.roomId, asUserId);
  }

  sendAsUser(userId: string, content: string): Promise<SendMessageResult> {
    return this.client.sendAsUser(this.roomId, userId, content);
  }

  sendSystemMessage(content: string, options?: SendSystemMessageOptions): Promise<SendMessageResult> {
    return this.client.sendSystemMessage(this.roomId, content, options);
  }

  getMessages(options: GetMessagesOptions): Promise<RoomHistoryResponse> {
    return this.client.getMessages(this.roomId, options);
  }

  events(options: StreamRoomEventsOptions): AsyncIterable<RoomEvent> {
    return this.client.streamRoomEvents(this.roomId, options);
  }

  redact(seq: number): Promise<RedactMessageResponse> {
    return this.client.redactMessage(this.roomId, seq);
  }
}
