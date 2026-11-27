using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dotwire.Nats;

public sealed class ErasureNotifierService(
    ErasureNoticePublisher publisher,
    INatsJSContext js,
    IOptions<NatsOptions> natsOptions,
    IOptions<WebhooksOptions> webhookOptions,
    IHttpClientFactory httpClientFactory,
    DotwireMetrics metrics,
    ILogger<ErasureNotifierService> logger) : BackgroundService
{
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    internal static TimeSpan DelayFor(ulong deliveries)
    {
        var index = (int)Math.Min(Math.Max(deliveries, 1UL) - 1, (ulong)Backoff.Length - 1);
        return Backoff[index];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!publisher.IsEnabled)
            return;

        var options = natsOptions.Value;
        var fetchOpts = new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(5) };
        INatsJSConsumer? consumer = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                consumer ??= await js.GetConsumerAsync(options.ErasureNoticesStream, options.ErasureNoticesConsumer, stoppingToken);
                await foreach (var msg in consumer.FetchAsync<byte[]>(fetchOpts, cancellationToken: stoppingToken))
                    await DeliverAsync(msg, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Erasure notifier loop error, retrying: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task DeliverAsync(INatsJSMsg<byte[]> msg, CancellationToken stoppingToken)
    {
        var erasure = webhookOptions.Value.Erasure;
        var body = msg.Data ?? [];
        var deliveries = msg.Metadata?.NumDelivered ?? 1UL;
        var streamSequence = msg.Metadata?.Sequence.Stream ?? 0UL;
        string reason;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(erasure.TimeoutMs));

            using var request = PresendWebhookClient.BuildRequest(erasure.Url!, body, "erasure");
            PresendWebhookClient.SignIfConfigured(request, body, webhookOptions.Value.Secret, DateTimeOffset.UtcNow);

            var client = httpClientFactory.CreateClient(PresendWebhookClient.HttpClientName);
            using var response = await client.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                await msg.AckAsync(cancellationToken: stoppingToken);
                return;
            }

            reason = $"status {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            reason = ex.Message;
        }

        metrics.ErasureNoticeFailed();
        logger.LogWarning("Erasure notice attempt {Attempt} failed ({Reason}) for stream sequence {StreamSequence}; retrying",
            deliveries, reason, streamSequence);
        await msg.NakAsync(new AckOpts { NakDelay = DelayFor(deliveries) }, stoppingToken);
    }
}
