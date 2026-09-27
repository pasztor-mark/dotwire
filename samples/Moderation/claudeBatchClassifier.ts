// Reference implementation of the @dotwire/host `ModerationClassifier` interface
// (packages/host/src/moderation.ts) on the Anthropic Message Batches API. Lives under
// samples/Moderation (no package.json, not part of any workspace) on purpose: it needs the
// `@anthropic-ai/sdk` package, which is NOT a dependency of this repo. Copy it into your
// host project, then:
//
//     npm install @anthropic-ai/sdk
//
// This file is illustrative only - it is not compiled or type-checked as part of this
// repo's build, and is not covered by any test in packages/host/test. Treat it as a
// starting point to adapt, not a drop-in you run unmodified.
//
// Why batches, and why Haiku: a moderation verdict is a plain classification, so the
// cheapest tier is plenty, and the Message Batches API is 50% off standard pricing on top
// of that. The policy prompt is identical for every request, so it is cached
// (cache_control) and its cost is paid roughly once per batch. The trade is latency:
// verdicts arrive in minutes, not seconds - which is fine for a second line of defense
// behind the cheap synchronous Presend checks (see samples/README.md for the full wiring).
//
// This is the TypeScript twin of samples/Moderation/ClaudeBatchModerationClassifier.cs;
// keep the request/response shape and polling behavior in sync with that file if either
// changes. Structured output here uses a forced tool call (`tool_choice: { type: 'tool',
// name: ... }`) rather than a JSON response-format flag, since that is the durable,
// widely-available way to get a deterministic JSON shape back from the Messages API - swap
// in a native structured-output flag if/when your SDK version documents one.

import Anthropic from '@anthropic-ai/sdk';
import type { ModerationClassifier, ModerationVerdict, PendingModeration } from '@dotwire/host';

const VERDICT_TOOL_NAME = 'submit_verdict';

const VERDICT_TOOL: Anthropic.Tool = {
  name: VERDICT_TOOL_NAME,
  description: 'Submit the moderation verdict for the message under review.',
  input_schema: {
    type: 'object',
    properties: {
      flagged: { type: 'boolean', description: 'true only if the message violates the policy' },
      category: {
        type: 'string',
        enum: ['none', 'harassment', 'hate', 'sexual', 'violence', 'self_harm', 'spam', 'other'],
      },
      reason: { type: 'string', description: 'one short sentence; empty when not flagged' },
    },
    required: ['flagged', 'category', 'reason'],
    additionalProperties: false,
  },
};

export interface ClaudeBatchClassifierOptions {
  client: Anthropic;
  policyPrompt: string;
  model?: string; // default 'claude-haiku-4-5'
  pollIntervalMs?: number; // default 15000
}

/** Reference `ModerationClassifier` on Anthropic's Message Batches API. */
export class ClaudeBatchModerationClassifier implements ModerationClassifier {
  private readonly client: Anthropic;
  private readonly policyPrompt: string;
  private readonly model: string;
  private readonly pollIntervalMs: number;

  constructor(options: ClaudeBatchClassifierOptions) {
    this.client = options.client;
    this.policyPrompt = options.policyPrompt;
    this.model = options.model ?? 'claude-haiku-4-5';
    this.pollIntervalMs = options.pollIntervalMs ?? 15_000;
  }

  async classify(
    batch: readonly PendingModeration[],
    signal?: AbortSignal
  ): Promise<readonly ModerationVerdict[]> {
    // custom_id is how results (returned in arbitrary order) map back to a redaction target.
    const pendingById = new Map(batch.map((m) => [customId(m), m]));

    const requests: Anthropic.Messages.Batches.BatchCreateParams.Request[] = batch.map((m) => ({
      custom_id: customId(m),
      params: {
        model: this.model,
        max_tokens: 256,
        system: [{ type: 'text', text: this.policyPrompt, cache_control: { type: 'ephemeral' } }],
        messages: [{ role: 'user', content: m.content }],
        tools: [VERDICT_TOOL],
        tool_choice: { type: 'tool', name: VERDICT_TOOL_NAME },
      },
    }));

    const created = await this.client.messages.batches.create({ requests }, { signal });

    // Most batches finish well under the hour ceiling; poll gently rather than busy-wait.
    let status = created;
    while (status.processing_status !== 'ended') {
      await delay(this.pollIntervalMs, signal);
      status = await this.client.messages.batches.retrieve(created.id, { signal });
    }

    const verdicts: ModerationVerdict[] = [];
    for await (const item of this.client.messages.batches.results(created.id, { signal })) {
      const pending = pendingById.get(item.custom_id);
      if (!pending) continue;

      // Errored / canceled / expired results are treated as "not flagged" here; a stricter
      // host can re-enqueue them instead. Never fail-closed by redacting on an API error.
      if (item.result.type !== 'succeeded') {
        verdicts.push({ roomId: pending.roomId, seq: pending.seq, flagged: false });
        continue;
      }

      const toolUse = item.result.message.content.find(
        (block): block is Anthropic.ToolUseBlock => block.type === 'tool_use' && block.name === VERDICT_TOOL_NAME
      );
      const input = toolUse?.input as { flagged?: boolean; category?: string; reason?: string } | undefined;

      verdicts.push(
        input
          ? { roomId: pending.roomId, seq: pending.seq, flagged: !!input.flagged, category: input.category, reason: input.reason }
          : { roomId: pending.roomId, seq: pending.seq, flagged: false }
      );
    }

    return verdicts;
  }
}

function customId(m: PendingModeration): string {
  return `${m.roomId}:${m.seq}`;
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener(
      'abort',
      () => {
        clearTimeout(timer);
        reject(signal.reason ?? new Error('aborted'));
      },
      { once: true }
    );
  });
}
