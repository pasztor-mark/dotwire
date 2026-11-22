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
            new NatsJSContext(connection), Options.Create(new NatsOptions()), metrics, NullLogger<JetStreamMetricsPoller>.Instance);

        await poller.PollOnceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Empty(poller.Snapshots);
    }
}
