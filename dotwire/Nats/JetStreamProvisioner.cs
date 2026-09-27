using Dotwire.Configuration;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dotwire.Nats;

/// <summary>
/// Idempotently converges JetStream streams/consumers at startup
/// (docs/pre-implementation.md §1.6). CreateOrUpdate semantics make re-runs boring;
/// compose healthchecks make the server reachable, but a retry loop still guards the
/// bare-metal case (mirrors MigrationRunner.OpenWithRetryAsync).
/// </summary>
public static class JetStreamProvisioner
{
    public static async Task ProvisionAsync(
        INatsJSContext js,
        NatsOptions options,
        ILogger logger,
        CancellationToken ct = default)
    {
        const int maxAttempts = 15;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ProvisionOnceAsync(js, options, ct);
                logger.LogInformation(
                    "JetStream provisioned: streams {Rooms}/{Audit}, consumer {Consumer}",
                    options.RoomsStream, options.AuditStream, options.PostgresWriterConsumer);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                logger.LogWarning("NATS not ready (attempt {Attempt}/{Max}): {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private static async Task ProvisionOnceAsync(
        INatsJSContext js, Dotwire.Configuration.NatsOptions options, CancellationToken ct)
    {
        await js.CreateOrUpdateStreamAsync(
            new StreamConfig(options.RoomsStream, [options.RoomsSubjectFilter])
            {
                Storage = StreamConfigStorage.File,
                MaxAge = TimeSpan.FromHours(options.RoomsMaxAgeHours),
            }, ct);

        await js.CreateOrUpdateStreamAsync(
            new StreamConfig(options.AuditStream, [options.AuditSubject])
            {
                Storage = StreamConfigStorage.File,
            }, ct);

        await js.CreateOrUpdateConsumerAsync(
            options.RoomsStream,
            new ConsumerConfig
            {
                Name = options.PostgresWriterConsumer,
                DurableName = options.PostgresWriterConsumer,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
            }, ct);

        // Single-writer hash chain: the durable pull consumer AuditWriterService fetches
        // from, guarded by a Postgres advisory lock so every API node can compete but only
        // one drains it at a time (spec §3.6).
        await js.CreateOrUpdateConsumerAsync(
            options.AuditStream,
            new ConsumerConfig
            {
                Name = options.AuditWriterConsumer,
                DurableName = options.AuditWriterConsumer,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
            }, ct);
    }
}
