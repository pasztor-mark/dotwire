using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dotwire.Nats;

public sealed class JetStreamMetricsPoller(
    INatsJSContext js,
    IOptions<NatsOptions> natsOptions,
    IOptions<AccessLogOptions> accessLog,
    DotwireMetrics metrics,
    ILogger<JetStreamMetricsPoller> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(10);

    private sealed class Pair(string stream, string consumer, string subject)
    {
        public string Stream { get; } = stream;
        public string Consumer { get; } = consumer;
        public string Subject { get; } = subject;
        public INatsJSStream? StreamHandle { get; set; }
        public INatsJSConsumer? ConsumerHandle { get; set; }
    }

    private Pair[]? _pairs;

    private Pair[] Pairs => _pairs ??= BuildPairs();

    internal IReadOnlyList<string> PairStreams => Pairs.Select(p => p.Stream).ToArray();

    private Pair[] BuildPairs()
    {
        var options = natsOptions.Value;
        var pairs = new List<Pair>
        {
            new(options.RoomsStream, options.PostgresWriterConsumer, options.RoomsSubjectFilter),
            new(options.AuditStream, options.AuditWriterConsumer, options.AuditSubject),
        };
        if (accessLog.Value.Enabled)
            pairs.Add(new Pair(options.AccessLogStream, options.AccessLogWriterConsumer, options.AccessLogSubject));
        return pairs.ToArray();
    }

    private volatile JetStreamSnapshot[] _snapshots = [];

    internal IReadOnlyList<JetStreamSnapshot> Snapshots => _snapshots;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!natsOptions.Value.Enabled)
            return;

        metrics.ObserveJetStream(() => Snapshots);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await PollOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var pairs = Pairs;
        var results = new List<JetStreamSnapshot>(pairs.Length);
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(PairTimeout);
            try
            {
                results.Add(await PollPairAsync(pair, linked.Token));
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                pair.StreamHandle = null;
                pair.ConsumerHandle = null;
                logger.LogWarning("JetStream metrics poll failed for {Stream} ({ErrorType}): {Error}",
                    pair.Stream, ex.GetType().Name, ex.Message);
            }
        }

        _snapshots = results.ToArray();
    }

    private async Task<JetStreamSnapshot> PollPairAsync(Pair pair, CancellationToken ct)
    {
        if (pair.StreamHandle is null)
            pair.StreamHandle = await js.GetStreamAsync(pair.Stream, cancellationToken: ct);
        else
            await pair.StreamHandle.RefreshAsync(ct);
        var stream = pair.StreamHandle;

        if (pair.ConsumerHandle is null)
            pair.ConsumerHandle = await js.GetConsumerAsync(pair.Stream, pair.Consumer, ct);
        else
            await pair.ConsumerHandle.RefreshAsync(ct);
        var consumer = pair.ConsumerHandle;

        var pending = (long)(consumer.Info.NumPending + (ulong)consumer.Info.NumAckPending);
        var age = 0.0;
        if (pending > 0)
        {
            try
            {
                var response = await stream.GetAsync(
                    new StreamMsgGetRequest { Seq = consumer.Info.AckFloor.StreamSeq + 1, NextBySubj = pair.Subject }, ct);
                age = Math.Max(0, (DateTimeOffset.UtcNow - response.Message.Time).TotalSeconds);
            }
            catch (NatsJSApiException ex) when (ex.Error.Code == 404 || ex.Error.ErrCode == 10037)
            {
            }
        }

        var info = stream.Info;
        return new JetStreamSnapshot(
            pair.Stream,
            pair.Consumer,
            pending,
            age,
            (long)info.State.Bytes,
            (long)info.State.Messages,
            info.Config.MaxBytes,
            info.Config.MaxAge.TotalSeconds);
    }
}
