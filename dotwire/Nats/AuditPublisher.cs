using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dotwire.Nats;

/// <summary>
/// Publishes <see cref="AuditEvent"/>s onto the AUDIT stream (spec §3.6). Endpoints call
/// this after their own mutation and surface a failed publish as 503 so the (idempotent)
/// caller retries - a missing audit event is unacceptable, a duplicate one is fine.
/// With <c>Nats:Enabled=false</c> this is a no-op, matching in-process tests that run
/// without NATS.
/// </summary>
public sealed class AuditPublisher(INatsJSContext js, IOptions<NatsOptions> natsOptions)
{
    public async Task PublishAsync(AuditEvent evt, CancellationToken ct)
    {
        var options = natsOptions.Value;
        if (!options.Enabled)
            return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, DotwireJsonContext.Default.AuditEvent);
        var ack = await js.PublishAsync<byte[]>(options.AuditSubject, payload, cancellationToken: ct);
        if (ack.Error is not null)
            throw new InvalidOperationException($"JetStream publish error {ack.Error.Code}");
    }
}
