using System.Diagnostics.Metrics;

namespace Dotwire.Configuration;

public sealed record JetStreamSnapshot(
    string Stream,
    string Consumer,
    long Pending,
    double OldestPendingAgeSeconds,
    long Bytes,
    long Messages,
    long MaxBytes,
    double MaxAgeSeconds);

public sealed class DotwireMetrics
{
    public const string MeterName = "Dotwire";

    private readonly Meter _meter;
    private readonly Counter<long> _auditUnavailable;
    private readonly Counter<long> _rejections;
    private readonly Counter<long> _presend;
    private readonly Counter<long> _postsendDropped;
    private readonly Counter<long> _fanoutDropped;
    private readonly Histogram<double> _sendDuration;
    private readonly Histogram<double> _publishDuration;
    private readonly Histogram<double> _fanoutLag;
    private readonly Counter<long> _authCache;
    private readonly Histogram<double> _authLookupDuration;
    private readonly Histogram<int> _authBatchSize;

    private static readonly InstrumentAdvice<double> LatencyBuckets = new()
    {
        HistogramBucketBoundaries = [0.0005, 0.001, 0.0025, 0.005, 0.0075, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
    };

    public DotwireMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _auditUnavailable = _meter.CreateCounter<long>("dotwire.audit.unavailable", "{response}");
        _rejections = _meter.CreateCounter<long>("dotwire.rejections", "{rejection}");
        _presend = _meter.CreateCounter<long>("dotwire.webhook.presend.results", "{call}");
        _postsendDropped = _meter.CreateCounter<long>("dotwire.webhook.postsend.dropped", "{event}");
        _fanoutDropped = _meter.CreateCounter<long>("dotwire.fanout.dropped", "{event}");
        _sendDuration = _meter.CreateHistogram("dotwire.message.send.duration", "s", null, null, LatencyBuckets);
        _publishDuration = _meter.CreateHistogram("dotwire.jetstream.publish.duration", "s", null, null, LatencyBuckets);
        _fanoutLag = _meter.CreateHistogram("dotwire.fanout.lag", "s", null, null, LatencyBuckets);
        _authCache = _meter.CreateCounter<long>("dotwire.auth.cache.lookups", "{lookup}");
        _authLookupDuration = _meter.CreateHistogram("dotwire.auth.lookup.duration", "s", null, null, LatencyBuckets);
        _authBatchSize = _meter.CreateHistogram("dotwire.auth.batch.size", "{lookup}", null, null,
            new InstrumentAdvice<int> { HistogramBucketBoundaries = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512] });
    }

    public void FanoutDropped(string transport, string reason) =>
        _fanoutDropped.Add(
            1,
            new KeyValuePair<string, object?>("transport", transport),
            new KeyValuePair<string, object?>("reason", reason));

    public void MessageSend(string transport, string outcome, TimeSpan elapsed) =>
        _sendDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("transport", transport),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void JetStreamPublish(string outcome, TimeSpan elapsed) =>
        _publishDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void FanoutLag(TimeSpan lag) =>
        _fanoutLag.Record(Math.Max(0, lag.TotalSeconds));

    public void AuthCacheLookup(bool hit) =>
        _authCache.Add(1, new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));

    public void AuthLookup(string path, TimeSpan elapsed) =>
        _authLookupDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("path", path));

    public void AuthBatch(int size) => _authBatchSize.Record(size);

    public void AuditUnavailable() => _auditUnavailable.Add(1);

    public void Rejection(string transport, string reason, string operation) =>
        _rejections.Add(
            1,
            new KeyValuePair<string, object?>("transport", transport),
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("operation", operation));

    public void PresendResult(string outcome) =>
        _presend.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void PostsendDropped(string reason) =>
        _postsendDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void ObserveAuditLockHeld(Func<bool> isHeld) =>
        _meter.CreateObservableGauge("dotwire.audit.writer.lock_held", () => isHeld() ? 1 : 0, "1");

    public void ObserveSseConnections(Func<long> count) =>
        _meter.CreateObservableGauge("dotwire.sse.active_connections", count, "{connection}");

    public void ObserveJetStream(Func<IReadOnlyList<JetStreamSnapshot>> snapshots)
    {
        Measurement<long> L(long v, string stream, string? consumer = null) =>
            consumer is null
                ? new(v, new KeyValuePair<string, object?>("stream", stream))
                : new(v, new KeyValuePair<string, object?>("stream", stream), new KeyValuePair<string, object?>("consumer", consumer));

        Measurement<double> D(double v, string stream, string? consumer = null) =>
            consumer is null
                ? new(v, new KeyValuePair<string, object?>("stream", stream))
                : new(v, new KeyValuePair<string, object?>("stream", stream), new KeyValuePair<string, object?>("consumer", consumer));

        _meter.CreateObservableGauge("dotwire.jetstream.consumer.pending",
            () => snapshots().Select(s => L(s.Pending, s.Stream, s.Consumer)).ToList(), "{message}");
        _meter.CreateObservableGauge("dotwire.jetstream.consumer.oldest_pending.age",
            () => snapshots().Select(s => D(s.OldestPendingAgeSeconds, s.Stream, s.Consumer)).ToList(), "s");
        _meter.CreateObservableGauge("dotwire.jetstream.stream.bytes",
            () => snapshots().Select(s => L(s.Bytes, s.Stream)).ToList(), "By");
        _meter.CreateObservableGauge("dotwire.jetstream.stream.messages",
            () => snapshots().Select(s => L(s.Messages, s.Stream)).ToList(), "{message}");
        _meter.CreateObservableGauge("dotwire.jetstream.stream.max_bytes",
            () => snapshots().Where(s => s.MaxBytes > 0).Select(s => L(s.MaxBytes, s.Stream)).ToList(), "By");
        _meter.CreateObservableGauge("dotwire.jetstream.stream.max_age",
            () => snapshots().Where(s => s.MaxAgeSeconds > 0).Select(s => D(s.MaxAgeSeconds, s.Stream)).ToList(), "s");
    }
}
