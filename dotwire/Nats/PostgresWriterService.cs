using System.Text;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Data;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Npgsql;

namespace Dotwire.Nats;

/// <summary>
/// Drains the durable postgres-writer consumer into the messages hypertable in batches
/// (≤ BatchMaxMessages or BatchLingerMs, whichever first - pre-implementation.md §1.6).
/// At-least-once + ON CONFLICT DO NOTHING on the (room_id, time, seq) PK = idempotent.
/// Acks only after commit; failures redeliver. History lags the JetStream ack by about
/// one batch interval by design - never "fix" that (ARCHITECTURE.md, "Write path").
/// </summary>
public sealed class PostgresWriterService(
    INatsJSContext js,
    IOptions<NatsOptions> natsOptions,
    [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
    ILogger<PostgresWriterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = natsOptions.Value;
        if (!options.Enabled)
            return;

        var consumer = await GetConsumerWithRetryAsync(options, stoppingToken);
        var fetchOpts = new NatsJSFetchOpts
        {
            MaxMsgs = options.BatchMaxMessages,
            Expires = TimeSpan.FromMilliseconds(options.BatchLingerMs),
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = new List<INatsJSMsg<byte[]>>(options.BatchMaxMessages);
                await foreach (var msg in consumer.FetchAsync<byte[]>(fetchOpts, cancellationToken: stoppingToken))
                    batch.Add(msg);

                if (batch.Count == 0)
                    continue;

                await InsertBatchAsync(batch, stoppingToken);

                foreach (var msg in batch)
                    await msg.AckAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Batch insert failed, awaiting redelivery: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task InsertBatchAsync(List<INatsJSMsg<byte[]>> batch, CancellationToken ct)
    {
        var sql = new StringBuilder(
            "INSERT INTO messages (room_id, time, seq, sender_id, key_id, content) VALUES ");
        await using var conn = await writeDataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        var validCount = 0;

        for (var i = 0; i < batch.Count; i++)
        {
            var message = JsonSerializer.Deserialize(
                batch[i].Data!, DotwireJsonContext.Default.RoomMessage);
            if (message is null || string.IsNullOrEmpty(message.SenderId) || string.IsNullOrEmpty(message.KeyId) || message.Content is null || message.Content.Length == 0)
                continue;

            var seq = (long)batch[i].Metadata!.Value.Sequence.Stream;
            var time = message.Time.Year >= 2020 ? message.Time.UtcDateTime : DateTime.UtcNow;

            var p = validCount * 6;
            if (validCount > 0) sql.Append(", ");
            sql.Append($"(${p + 1}, ${p + 2}, ${p + 3}, ${p + 4}, ${p + 5}, ${p + 6})");
            cmd.Parameters.AddWithValue(message.RoomId);
            cmd.Parameters.AddWithValue(time);
            cmd.Parameters.AddWithValue(seq);
            cmd.Parameters.AddWithValue((object?)message.SenderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)message.KeyId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)message.Content ?? DBNull.Value);
            validCount++;
        }

        if (validCount == 0)
            return;

        sql.Append(" ON CONFLICT DO NOTHING");
        cmd.CommandText = sql.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<INatsJSConsumer> GetConsumerWithRetryAsync(NatsOptions options, CancellationToken ct)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await js.GetConsumerAsync(options.RoomsStream, options.PostgresWriterConsumer, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                logger.LogWarning("Consumer {Consumer} not available yet (attempt {Attempt}/{Max}): {Message}",
                    options.PostgresWriterConsumer, attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
