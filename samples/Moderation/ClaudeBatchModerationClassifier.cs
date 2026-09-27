// Reference implementation of Dotwire.Host.Moderation.IModerationClassifier on the Claude
// Message Batches API. Lives under samples/ (no csproj) on purpose: it needs the `Anthropic`
// NuGet package, which the host app adds itself. Copy it into your host project, then:
//
//     dotnet add package Anthropic
//
// Why batches, and why Haiku: a moderation verdict is a plain classification, so the cheapest
// tier is plenty, and the Batch API is 50% off standard pricing on top of that. The policy
// prompt is identical for every request, so it is cached (cache_control) and its cost is paid
// roughly once per batch. The trade is latency: verdicts arrive in minutes, not seconds -
// which is fine for a second line of defense behind the cheap synchronous Presend checks.
//
// Written against the SDK's documented surface (BatchCreateParams / MessageBatch /
// MessageBatchIndividualResponse / IBatchService). The two spots most likely to need a touch
// when your SDK version differs are marked "SDK:" below - the compiler will point at them.

using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Anthropic.Models.Messages.Batches;
using Dotwire.Host.Moderation;

namespace Samples.Moderation;

public sealed class ClaudeBatchModerationClassifier(
    AnthropicClient client,
    string policyPrompt,
    string model = "claude-haiku-4-5",
    TimeSpan? pollInterval = null) : IModerationClassifier
{
    // Structured output: the model can only answer in this shape, so parsing is deterministic.
    private static readonly Dictionary<string, JsonElement> VerdictSchema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            flagged = new { type = "boolean", description = "true only if the message violates the policy" },
            category = new
            {
                type = "string",
                @enum = new[] { "none", "harassment", "hate", "sexual", "violence", "self_harm", "spam", "other" },
            },
            reason = new { type = "string", description = "one short sentence; empty when not flagged" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "flagged", "category", "reason" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public async Task<IReadOnlyList<ModerationVerdict>> ClassifyAsync(
        IReadOnlyList<PendingModeration> batch, CancellationToken ct)
    {
        // custom_id is how results (returned in arbitrary order) map back to a redaction target.
        var pendingById = batch.ToDictionary(m => CustomId(m));

        var requests = batch.Select(m => new BatchCreateParams.Request
        {
            CustomID = CustomId(m),
            Params = new()
            {
                Model = model,
                MaxTokens = 256,
                // SDK: ParamsSystem accepts a List<TextBlockParam> (same shape as MessageCreateParams.System).
                System = new List<TextBlockParam>
                {
                    new() { Text = policyPrompt, CacheControl = new CacheControlEphemeral() },
                },
                Messages = [new() { Role = Role.User, Content = m.Content }],
                OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = VerdictSchema } },
            },
        }).ToList();

        var created = await client.Messages.Batches.Create(new BatchCreateParams { Requests = requests }, ct);

        // Most batches finish well under the hour ceiling; poll gently rather than busy-wait.
        var interval = pollInterval ?? TimeSpan.FromSeconds(15);
        var status = created;
        // SDK: ProcessingStatus is ApiEnum<string, ProcessingStatus>; compare against the enum value.
        while (status.ProcessingStatus != ProcessingStatus.Ended)
        {
            await Task.Delay(interval, ct);
            status = await client.Messages.Batches.Retrieve(created.ID, cancellationToken: ct);
        }

        var verdicts = new List<ModerationVerdict>(batch.Count);
        await foreach (var item in client.Messages.Batches.ResultsStreaming(created.ID, cancellationToken: ct))
        {
            if (!pendingById.TryGetValue(item.CustomID, out var pending))
                continue;

            // Errored / canceled / expired results are treated as "not flagged" here; a stricter
            // host can re-enqueue them instead. Never fail-closed by redacting on an API error.
            if (!item.Result.TryPickSucceeded(out var succeeded) || succeeded is null)
            {
                verdicts.Add(new ModerationVerdict(pending.RoomId, pending.Seq, Flagged: false));
                continue;
            }

            var text = succeeded.Message.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .FirstOrDefault()?.Text;

            var parsed = text is null ? null : JsonSerializer.Deserialize<VerdictJson>(text);
            verdicts.Add(parsed is null
                ? new ModerationVerdict(pending.RoomId, pending.Seq, Flagged: false)
                : new ModerationVerdict(pending.RoomId, pending.Seq, parsed.flagged, parsed.category, parsed.reason));
        }

        return verdicts;
    }

    private static string CustomId(PendingModeration m) => $"{m.RoomId}:{m.Seq}";

    private sealed record VerdictJson(bool flagged, string category, string reason);
}
