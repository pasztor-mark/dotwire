import { createHash } from 'node:crypto';
import type { AuditEntry } from './types.js';

export const GENESIS_HASH = '0'.repeat(64);

const UTC_TIME = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(?:Z|\+00:00)$/;

export function auditCanonicalForm(entry: AuditEntry): string {
  const match = UTC_TIME.exec(entry.time);
  if (!match) throw new Error(`audit time is not a UTC timestamp: ${entry.time}`);
  const fraction = (match[2] ?? '').padEnd(6, '0').slice(0, 6);
  return [
    entry.eventType,
    entry.roomId ?? '',
    entry.messageSeq ?? '',
    entry.actorId ?? '',
    entry.subjectId ?? '',
    entry.value ?? '',
    `${match[1]}.${fraction}Z`
  ].join('\n');
}

export function auditChainHash(previousHash: string, entry: AuditEntry): string {
  if (!/^[0-9a-f]{64}$/.test(previousHash)) throw new Error('previous audit hash must be 64 lowercase hex characters');
  return createHash('sha256').update(Buffer.from(previousHash, 'hex')).update(auditCanonicalForm(entry), 'utf8').digest('hex');
}
