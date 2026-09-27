using System.Text.Json;
using System.Threading.Channels;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Nats;

/// <summary>
/// Best-effort postsend webhook delivery (spec §3.8): a bounded channel drains into HTTP
/// calls with exponential backoff, never blocking the send path. A full queue drops the
/// newest item with a warning; delivery failure after `MaxAttempts` is dropped with a warning
/// - "the host treats it as a second line of defense, never the record."
/// </summary>
public sealed class PostSendWebhookService : BackgroundService
{
    private readonly Channel<PostSendWebhookRequest> _channel;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<WebhooksOptions> _options;
    private readonly ILogger<PostSendWebhookService> _logger;

    public PostSendWebhookService(IHttpClientFactory httpClientFactory, IOptions<WebhooksOptions> options, ILogger<PostSendWebhookService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _channel = Channel.CreateBounded<PostSendWebhookRequest>(new BoundedChannelOptions(Math.Max(1, options.Value.PostSend.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_options.Value.PostSend.Url);

    /// <summary>Enqueues a postsend delivery; never blocks or throws. Drops with a warning if the queue is full.</summary>
    public void Enqueue(PostSendWebhookRequest request)
    {
        var postSend = _options.Value.PostSend;
        if (string.IsNullOrEmpty(postSend.Url))
            return;

        if (!_channel.Writer.TryWrite(request))
            _logger.LogWarning("Postsend webhook queue is full (capacity {Capacity}); dropping event for room {RoomId} seq {Seq}",
                postSend.QueueCapacity, request.RoomId, request.Seq);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverAsync(request, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Unexpected postsend delivery error for room {RoomId} seq {Seq}: {Error}",
                    request.RoomId, request.Seq, ex.Message);
            }
        }
    }

    private async Task DeliverAsync(PostSendWebhookRequest request, CancellationToken stoppingToken)
    {
        var postSend = _options.Value.PostSend;
        var body = JsonSerializer.SerializeToUtf8Bytes(request, DotwireJsonContext.Default.PostSendWebhookRequest);
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= postSend.MaxAttempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(postSend.TimeoutMs));

                using var httpRequest = PresendWebhookClient.BuildRequest(postSend.Url!, body, "postsend");
                PresendWebhookClient.SignIfConfigured(httpRequest, body, _options.Value.Secret);

                var client = _httpClientFactory.CreateClient(PresendWebhookClient.HttpClientName);
                using var response = await client.SendAsync(httpRequest, cts.Token);
                if (response.IsSuccessStatusCode)
                    return;

                _logger.LogWarning("Postsend webhook attempt {Attempt}/{Max} for room {RoomId} seq {Seq} got {Status}",
                    attempt, postSend.MaxAttempts, request.RoomId, request.Seq, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Postsend webhook attempt {Attempt}/{Max} for room {RoomId} seq {Seq} failed: {Error}",
                    attempt, postSend.MaxAttempts, request.RoomId, request.Seq, ex.Message);
            }

            if (attempt < postSend.MaxAttempts)
            {
                await Task.Delay(delay, stoppingToken);
                delay *= 2;
            }
        }

        _logger.LogWarning("Postsend webhook dropped after {Max} attempts for room {RoomId} seq {Seq}",
            postSend.MaxAttempts, request.RoomId, request.Seq);
    }
}
