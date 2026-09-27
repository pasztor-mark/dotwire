# Samples

Illustrative, uncompiled reference code — not built, not tested, not part of any workspace
or `.csproj`. Copy into your own host project and adapt; each file's header says exactly
what extra package it needs to actually run.

## Moderation

Both host SDKs (`Dotwire.Host`, `@dotwire/host`) ship two moderation hooks and a bounded
queue/worker pair, but deliberately no built-in classifier — "AI moderation" is host policy,
not a dotwire concern (PRODUCT.md, "Out of scope"). These samples show the complete wiring
end to end, in both languages, using Anthropic's Claude Message Batches API as the
asynchronous classifier.

The pipeline is the same shape in both languages:

1. **Presend** — a cheap, synchronous, blocking check (blocklist, regex, per-user rate
   limit) that runs before a message is accepted. Can reject the send outright or rewrite
   content. Runs either inline (direct SDK sends) or behind dotwire's `Dotwire:Webhooks:
   Presend:Url` webhook call-out if you point it at your host instead.
2. **Postsend** — a microtask/channel-cheap hand-off into a bounded in-process queue,
   running after a message is already accepted (it has a `(roomId, seq)`). Adds no
   meaningful latency to the send path. Runs inline or behind `Dotwire:Webhooks:PostSend:Url`.
3. **Batch worker** — drains the queue in size-or-time bounded batches, hands each batch to
   a `ModerationClassifier`/`IModerationClassifier`, and redacts every flagged message
   through the admin API (retrying on `429` with `Retry-After`). This is where the sample
   classifiers below plug in.

### .NET: `Moderation/ClaudeBatchModerationClassifier.cs`

Implements `Dotwire.Host.Moderation.IModerationClassifier`. Needs the `Anthropic` NuGet
package (`dotnet add package Anthropic` in your own host project — not a dependency of this
repo). See [`Dotwire.Host/README.md`, "Moderation: two hooks, two speeds"](../Dotwire.Host/README.md#moderation-two-hooks-two-speeds)
for the full `Presend`/`PostSend`/webhook/`BatchModerationWorker` wiring this classifier
plugs into:

```csharp
client.Presend = (ctx, ct) => Task.FromResult(
    Blocklist.Hits(ctx.Content) ? PresendResult.Reject("blocked term") : PresendResult.Accept());

var queue = new ModerationQueue();
client.PostSend = queue.PostSendHook;

IModerationClassifier classifier = new ClaudeBatchModerationClassifier(anthropic, policyPrompt);
var worker = new BatchModerationWorker(queue, classifier, BatchModerationWorker.RedactWith(client));
await worker.RunAsync(stoppingToken); // e.g. inside a BackgroundService
```

### TypeScript: `Moderation/claudeBatchClassifier.ts`

Implements the `ModerationClassifier` interface from `packages/host/src/moderation.ts`.
Needs `@anthropic-ai/sdk` (`npm install @anthropic-ai/sdk` in your own host project — not a
dependency of this repo). See [`packages/host/README.md`](../packages/host/README.md) for
the full `presend`/`postSend`/webhook handler wiring; the classifier plugs into the same
`ModerationQueue`/`BatchModerationWorker` pair as its .NET twin:

```typescript
host.presend = async (ctx) =>
  Blocklist.hits(ctx.content) ? { action: 'reject', reason: 'blocked term' } : { action: 'accept' };

const queue = new ModerationQueue();
host.postSend = queue.postSendHook;

const classifier = new ClaudeBatchModerationClassifier({ client: anthropic, policyPrompt });
const worker = new BatchModerationWorker(queue, classifier, BatchModerationWorker.redactWith(host));
await worker.run(abortController.signal);
```

### Why batches, and why Haiku

A moderation verdict is a plain classification, so the cheapest model tier is plenty, and
the Message Batches API is 50% off standard pricing on top of that. The policy prompt is
identical for every request in a batch, so both samples mark it `cache_control: ephemeral`
and pay its cost roughly once per batch rather than once per message. The trade is latency —
verdicts arrive in minutes, not seconds — which is the right shape for a *second* line of
defense sitting behind the cheap synchronous `Presend` checks, never the first.
