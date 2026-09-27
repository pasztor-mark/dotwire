import type { DotwireClient } from '@dotwire/client';

/**
 * `DotwireClient.subscribe`/`unsubscribe` (and `DotwireRoomHandle.subscribe`/`unsubscribe`)
 * operate on a single, client-wide "am I in this room's hub group" flag per `roomId` — it is
 * not refcounted. If two hooks (e.g. `useRoom` and `useTyping`) are both mounted against the
 * same room, calling `subscribe` from each is harmless (redundant hub invokes), but the FIRST
 * hook to unmount would call `unsubscribe` and silently drop the OTHER hook's live delivery.
 *
 * This module refcounts unsubscribe intent per `(client, roomId)` pair so the underlying hub
 * group is only actually left once every hook interested in that room has released it. It does
 * not wrap `subscribe` itself, since redundant `Subscribe` hub invokes are cheap and safe.
 */
const refCounts = new WeakMap<DotwireClient, Map<string, number>>();

export function retainRoom(client: DotwireClient, roomId: string): void {
  let rooms = refCounts.get(client);
  if (!rooms) {
    rooms = new Map();
    refCounts.set(client, rooms);
  }
  rooms.set(roomId, (rooms.get(roomId) ?? 0) + 1);
}

/**
 * Releases one hook's interest in `roomId`. Returns `true` when this was the last interested
 * hook, meaning the caller should now call `client.unsubscribe(roomId)`.
 */
export function releaseRoom(client: DotwireClient, roomId: string): boolean {
  const rooms = refCounts.get(client);
  if (!rooms) return true;
  const next = (rooms.get(roomId) ?? 1) - 1;
  if (next <= 0) {
    rooms.delete(roomId);
    return true;
  }
  rooms.set(roomId, next);
  return false;
}
