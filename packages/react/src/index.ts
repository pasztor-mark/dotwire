export { DotwireProvider, DotwireContext, type DotwireProviderProps } from './provider.js';
export {
  useDotwireClient,
  useConnectionState,
  useRoom,
  useTyping,
  usePresence,
  type UseRoomOptions,
  type UseRoomResult,
  type UseTypingResult,
  type UsePresenceResult
} from './hooks.js';
export * from './reducers.js';

// Re-exported for convenience so consumers of `@dotwire/react` do not also need a direct
// dependency on `@dotwire/client` just to type `options`/`client` props or hook results.
export type {
  ConnectionState,
  DotwireClient,
  DotwireClientOptions,
  HistoryPage,
  MembershipRevoked,
  MessageRetracted,
  PresenceDelta,
  RoomMessage,
  SendAck,
  TypingNotification
} from '@dotwire/client';
