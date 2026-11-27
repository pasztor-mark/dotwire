using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dotwire.Nats;

public class ErasureNoticePublisher(INatsJSContext js, IOptions<NatsOptions> natsOptions, IOptions<WebhooksOptions> webhookOptions)
{
    public const int MaxMessagesPerNotice = 1000;

    public bool IsEnabled => natsOptions.Value.Enabled && !string.IsNullOrEmpty(webhookOptions.Value.Erasure.Url);

    public virtual async Task PublishAsync(ErasureNotice notice, CancellationToken ct)
    {
        if (!IsEnabled)
            return;

        if (notice.Messages is { Length: > MaxMessagesPerNotice })
            throw new ArgumentException($"An erasure notice carries at most {MaxMessagesPerNotice} messages.", nameof(notice));

        var payload = JsonSerializer.SerializeToUtf8Bytes(notice, DotwireJsonContext.Default.ErasureNotice);
        var ack = await js.PublishAsync<byte[]>(natsOptions.Value.ErasureNoticesSubject, payload, cancellationToken: ct);
        if (ack.Error is not null)
            throw new InvalidOperationException($"JetStream publish error {ack.Error.Code}");
    }
}
