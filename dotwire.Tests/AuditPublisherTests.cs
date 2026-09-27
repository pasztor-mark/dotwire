using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Xunit;

namespace dotwire.Tests;

/// <summary>With <c>Nats:Enabled=false</c> the publisher never touches JetStream (in-process tests run without NATS).</summary>
public class AuditPublisherTests
{
    [Fact]
    public async Task PublishAsync_NoOpsWhenNatsDisabled()
    {
        // A null INatsJSContext would throw on any use; passing it proves PublishAsync
        // returns before ever touching the JetStream context when Nats:Enabled is false.
        var publisher = new AuditPublisher(null!, Options.Create(new NatsOptions { Enabled = false }));

        var evt = new AuditEvent("audit.read", null, null, "auditor-1", null, null, DateTimeOffset.UtcNow);

        await publisher.PublishAsync(evt, CancellationToken.None);
    }
}
