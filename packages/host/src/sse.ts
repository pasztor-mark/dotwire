import type { RoomEvent } from './types.js';

interface RawSseEvent {
  event: string;
  data: string;
  id?: string;
}

/**
 * Parses a `text/event-stream` body into raw SSE events, per spec §3.9's wire format:
 * `id:`/`event:`/`data:` lines, blank-line terminated, `: ping` comment lines ignored.
 * Tolerant of both `\n` and `\r\n` line endings.
 */
export async function* parseSseStream(
  body: ReadableStream<Uint8Array>,
  signal?: AbortSignal
): AsyncGenerator<RawSseEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    while (true) {
      if (signal?.aborted) return;
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n');

      let idx: number;
      while ((idx = buffer.indexOf('\n\n')) !== -1) {
        const block = buffer.slice(0, idx);
        buffer = buffer.slice(idx + 2);
        const parsed = parseEventBlock(block);
        if (parsed) yield parsed;
      }
    }
  } finally {
    try {
      reader.releaseLock();
    } catch {
      // already released/closed
    }
  }
}

function parseEventBlock(block: string): RawSseEvent | null {
  let eventName = 'message';
  let id: string | undefined;
  const dataLines: string[] = [];
  let sawData = false;

  for (const line of block.split('\n')) {
    if (line.length === 0) continue;
    if (line.startsWith(':')) continue; // comment / keep-alive ping, e.g. ": ping"
    if (line.startsWith('event:')) {
      eventName = line.slice('event:'.length).trim();
    } else if (line.startsWith('id:')) {
      id = line.slice('id:'.length).trim();
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice('data:'.length).trim());
      sawData = true;
    }
  }

  if (!sawData) return null;
  return { event: eventName, data: dataLines.join('\n'), id };
}

/** Converts one raw SSE event to a typed {@link RoomEvent}, or null for anything unrecognized. */
export function toRoomEvent(raw: RawSseEvent): RoomEvent | null {
  let data: unknown;
  try {
    data = JSON.parse(raw.data);
  } catch {
    return null;
  }
  if (typeof data !== 'object' || data === null) return null;
  const d = data as Record<string, unknown>;

  switch (raw.event) {
    case 'message':
      return {
        type: 'message',
        roomId: String(d.roomId),
        seq: Number(d.seq),
        senderId: String(d.senderId),
        time: String(d.time),
        content: String(d.content),
        replayed: d.replayed === true
      };
    case 'retracted':
      return { type: 'retracted', roomId: String(d.roomId), seq: Number(d.seq) };
    case 'typing':
      return { type: 'typing', roomId: String(d.roomId), userId: String(d.userId), isTyping: Boolean(d.isTyping) };
    case 'presence':
      return {
        type: 'presence',
        roomId: String(d.roomId),
        joined: Array.isArray(d.joined) ? d.joined.map(String) : [],
        left: Array.isArray(d.left) ? d.left.map(String) : []
      };
    case 'revoked':
      return { type: 'revoked', roomId: String(d.roomId) };
    default:
      return null;
  }
}
