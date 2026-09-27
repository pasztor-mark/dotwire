/**
 * Bounded, insertion-ordered set of recently-seen sequence numbers, used for per-room dedupe
 * (spec §6: "a bounded seen-set of the last 1000 seqs"). Backed by a `Set`, which preserves
 * insertion order in JS, so the oldest entry is always `values().next().value`.
 */
export class BoundedSeqSet {
  private readonly seen = new Set<number>();

  constructor(private readonly capacity = 1000) {}

  public has(seq: number): boolean {
    return this.seen.has(seq);
  }

  public add(seq: number): void {
    if (this.seen.has(seq)) return;
    this.seen.add(seq);
    if (this.seen.size > this.capacity) {
      const oldest = this.seen.values().next().value;
      if (oldest !== undefined) {
        this.seen.delete(oldest);
      }
    }
  }
}
