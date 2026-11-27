using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Xunit;

namespace dotwire.Tests;

public class JetStreamMetricsPollerTests
{
    [Fact]
    public async Task PollOnce_NatsUnreachable_ReturnsEmptyWithoutThrowing()
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:1" });
        var (metrics, _) = TestMetrics.Create();
        var poller = new JetStreamMetricsPoller(
            new NatsJSContext(connection), Options.Create(new NatsOptions()), Options.Create(new AccessLogOptions()), metrics, NullLogger<JetStreamMetricsPoller>.Instance);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Empty(poller.Snapshots);
    }

    [Theory]
    [InlineData(false, new[] { "ROOMS", "AUDIT" })]
    [InlineData(true, new[] { "ROOMS", "AUDIT", "ACCESS" })]
    public void PairStreams_IncludeAccessOnlyWhenEnabled(bool enabled, string[] expected)
    {
        var (metrics, _) = TestMetrics.Create();
        var nats = new NatsOptions { RoomsStream = "ROOMS", AuditStream = "AUDIT", AccessLogStream = "ACCESS" };
        var poller = new JetStreamMetricsPoller(
            null!, Options.Create(nats), Options.Create(new AccessLogOptions { Enabled = enabled }), metrics,
            NullLogger<JetStreamMetricsPoller>.Instance);

        Assert.Equal(expected, poller.PairStreams);
    }
}
