using System.Reflection;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace dotwire.Tests;

public class AccessLogPublisherTests
{
    public class FakeJs : DispatchProxy
    {
        public Func<int, string, byte[], PubAckResponse>? Handler;
        public Func<CancellationToken, ValueTask<PubAckResponse>>? Hang;
        public int Calls;
        public readonly List<(string Subject, byte[] Payload)> Published = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != "PublishAsync")
                throw new NotSupportedException(targetMethod.Name);
            var n = Interlocked.Increment(ref Calls);
            var subject = (string)args![0]!;
            var payload = (byte[])args[1]!;
            lock (Published)
                Published.Add((subject, payload));
            if (Hang is not null)
                return Hang(args.OfType<CancellationToken>().Last());
            return new ValueTask<PubAckResponse>(Handler!(n, subject, payload));
        }
    }

    private static AccessEvent Opened(Guid? window = null) => new(
        AccessEvents.Opened, DateTimeOffset.UtcNow, Guid.NewGuid(), "u1", null, "member", AccessEvents.Hub,
        window ?? Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));

    private static AccessLogPublisher Create(INatsJSContext? js, DotwireMetrics metrics, bool enabled = true) => new(
        js!,
        Options.Create(new NatsOptions { Enabled = true }),
        Options.Create(new AccessLogOptions { Enabled = enabled }),
        metrics,
        NullLogger<AccessLogPublisher>.Instance);

    [Fact]
    public async Task CancelledOpen_QueuesErrorClose_AndRethrowsCancellation()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        var fake = (FakeJs)(object)js;
        fake.Handler = (n, _, _) => n == 1
            ? throw new OperationCanceledException()
            : new PubAckResponse { Stream = "ACCESS", Seq = 2 };
        var publisher = Create(js, metrics);
        var window = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.RecordAsync(Opened(window), cts.Token));

        Assert.Equal(0, capture.Count("dotwire.access_log.unavailable", ("event", "opened")));
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 100 && fake.Calls < 2; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await publisher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.Calls);
        var closed = JsonSerializer.Deserialize(fake.Published[1].Payload, DotwireJsonContext.Default.AccessEvent)!;
        Assert.Equal(AccessEvents.Closed, closed.Event);
        Assert.Equal(AccessEvents.Error, closed.Reason);
        Assert.Equal(window, closed.WindowId);
    }

    [Fact]
    public async Task CancelledHistoryRead_QueuesNoClose()
    {
        var (metrics, _) = TestMetrics.Create();
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        var fake = (FakeJs)(object)js;
        fake.Handler = (_, _, _) => throw new OperationCanceledException();
        var publisher = Create(js, metrics);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var read = Opened() with { Event = AccessEvents.HistoryRead, WindowId = null };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.RecordAsync(read, cts.Token));
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await publisher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Disabled_NoOps()
    {
        var (metrics, _) = TestMetrics.Create();
        var publisher = Create(null, metrics, enabled: false);

        Assert.False(publisher.IsEnabled);
        await publisher.RecordAsync(Opened(), CancellationToken.None);
        publisher.RecordClose(Opened());
    }

    private static async ValueTask<PubAckResponse> HangUntilCancelled(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return new PubAckResponse();
    }

    [Fact]
    public async Task HungPublish_TimesOut_AsAccessLogUnavailable()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        ((FakeJs)(object)js).Hang = HangUntilCancelled;
        var publisher = Create(js, metrics);

        var task = publisher.RecordAsync(Opened(), CancellationToken.None);
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));

        Assert.Same(task, done);
        await Assert.ThrowsAsync<AccessLogUnavailableException>(() => task);
        Assert.Equal(1, capture.Count("dotwire.access_log.unavailable", ("event", "opened")));
    }

    [Fact]
    public async Task HungPublish_CallerCancellation_StillThrowsOperationCanceled()
    {
        var (metrics, _) = TestMetrics.Create();
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        ((FakeJs)(object)js).Hang = HangUntilCancelled;
        var publisher = Create(js, metrics);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.RecordAsync(Opened(), cts.Token));
    }

    [Fact]
    public async Task FailedOpen_QueuesErrorClose_AndThrows()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        var fake = (FakeJs)(object)js;
        fake.Handler = (n, _, _) => n == 1
            ? throw new InvalidOperationException("down")
            : new PubAckResponse { Stream = "ACCESS", Seq = 2 };
        var publisher = Create(js, metrics);
        var window = Guid.NewGuid();

        await Assert.ThrowsAsync<AccessLogUnavailableException>(() => publisher.RecordAsync(Opened(window), CancellationToken.None));

        Assert.Equal(1, capture.Count("dotwire.access_log.unavailable", ("event", "opened")));
        await publisher.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 100 && fake.Calls < 2; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await publisher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.Calls);
        var closed = JsonSerializer.Deserialize(fake.Published[1].Payload, DotwireJsonContext.Default.AccessEvent)!;
        Assert.Equal(AccessEvents.Closed, closed.Event);
        Assert.Equal(AccessEvents.Error, closed.Reason);
        Assert.Equal(window, closed.WindowId);
    }

    [Fact]
    public async Task RunningPublisher_FailingClose_RetriedThreeTimes_ThenCountedDropped()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        var fake = (FakeJs)(object)js;
        fake.Handler = (_, _, _) => throw new InvalidOperationException("down");
        var publisher = Create(js, metrics);
        publisher.RecordClose(Opened());

        await publisher.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 100 && capture.Count("dotwire.access_log.close_dropped", ("reason", "publish_failed")) < 1; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await publisher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, fake.Calls);
        Assert.Equal(1, capture.Count("dotwire.access_log.close_dropped", ("reason", "publish_failed")));
    }

    [Fact]
    public async Task HungPublish_ClosesLostAtShutdown_AreCounted()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        var fake = (FakeJs)(object)js;
        fake.Hang = HangUntilCancelled;
        var publisher = Create(js, metrics);
        for (var i = 0; i < 3; i++)
            publisher.RecordClose(Opened());

        await publisher.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 100 && fake.Calls < 1; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await publisher.StopAsync(TestContext.Current.CancellationToken);

        publisher.RecordClose(Opened());
        Assert.Equal(4, capture.Count("dotwire.access_log.close_dropped", ("reason", "publish_failed")));
    }

    [Fact]
    public async Task FullCloseQueue_CountsQueueFull()
    {
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var publisher = Create(null, metrics);

        for (var i = 0; i < 10_001; i++)
            publisher.RecordClose(Opened());

        Assert.True(capture.Count("dotwire.access_log.close_dropped", ("reason", "queue_full")) >= 1);
        await Task.CompletedTask;
    }
}
