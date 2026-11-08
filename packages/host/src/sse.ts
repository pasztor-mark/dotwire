import type { RoomEvent } from './types.js';

interface RawSseEvent {
  event: string;
  data: string;
  id?: string;
}

export const MAX_SSE_BUFFER_CHARS = 1024 * 1024;

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
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      const pendingCr = !done && buffer.endsWith('\r');
      if (pendingCr) buffer = buffer.slice(0, -1);
      buffer = buffer.replace(/\r\n?/g, '\n');
      if (pendingCr) buffer += '\r';

      let idx: number;
      while ((idx = buffer.indexOf('\n\n')) !== -1) {
        const block = buffer.slice(0, idx);
        buffer = buffer.slice(idx + 2);
        const parsed = parseEventBlock(block);
        if (parsed) yield parsed;
      }

      if (done) break;
      if (buffer.length > MAX_SSE_BUFFER_CHARS) {
        throw new Error('SSE event exceeds the maximum buffered size');
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

function fieldValue(line: string, name: string): string {
  const value = line.slice(name.length + 1);
  return value.startsWith(' ') ? value.slice(1) : value;
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
      eventName = fieldValue(line, 'event');
    } else if (line.startsWith('id:')) {
      id = fieldValue(line, 'id');
    } else if (line.startsWith('data:')) {
      dataLines.push(fieldValue(line, 'data'));
      sawData = true;
    }
  }

  if (!sawData) return null;
  return { event: eventName, data: dataLines.join('\n'), id };
}

function str(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function seqOf(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null;
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
  const roomId = str(d.roomId);
  if (roomId === null) return null;

  switch (raw.event) {
    case 'message': {
      const seq = seqOf(d.seq);
      const senderId = str(d.senderId);
      const time = str(d.time);
      const content = str(d.content);
      if (seq === null || senderId === null || time === null || content === null) return null;
      return { type: 'message', roomId, seq, senderId, time, content, replayed: d.replayed === true };
    }
    case 'retracted': {
      const seq = seqOf(d.seq);
      return seq === null ? null : { type: 'retracted', roomId, seq };
    }
    case 'typing': {
      const userId = str(d.userId);
      return userId === null ? null : { type: 'typing', roomId, userId, isTyping: d.isTyping === true };
    }
    case 'presence':
      return {
        type: 'presence',
        roomId,
        joined: Array.isArray(d.joined) ? d.joined.filter((u): u is string => typeof u === 'string') : [],
        left: Array.isArray(d.left) ? d.left.filter((u): u is string => typeof u === 'string') : []
      };
    case 'revoked':
      return { type: 'revoked', roomId };
    default:
      return null;
  }
}
