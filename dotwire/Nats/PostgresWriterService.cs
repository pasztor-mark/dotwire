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

                await ProcessBatchAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Batch insert failed ({ErrorType}), awaiting redelivery: {Error}", ex.GetType().Name, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ProcessBatchAsync(List<INatsJSMsg<byte[]>> batch, CancellationToken ct)
    {
        var rows = new List<(INatsJSMsg<byte[]> Msg, RoomMessage Message, long Seq)>(batch.Count);
        foreach (var msg in batch)
        {
            var seq = (long)msg.Metadata!.Value.Sequence.Stream;
            if (TryParse(msg.Data) is { } message)
            {
                rows.Add((msg, message, seq));
                continue;
            }

            logger.LogError("Dropping malformed room message at stream seq {Seq}", seq);
            await msg.AckTerminateAsync(cancellationToken: ct);
        }

        if (rows.Count == 0)
            return;

        try
        {
            await InsertAsync(rows.Select(r => (r.Message, r.Seq)).ToList(), ct);
        }
        catch (PostgresException ex) when (IsPermanent(ex) && rows.Count > 1)
        {
            logger.LogWarning("Batch insert rejected ({SqlState}); retrying {Count} messages one by one", ex.SqlState, rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    await InsertAsync([(row.Message, row.Seq)], ct);
                }
                catch (PostgresException single) when (IsPermanent(single))
                {
                    logger.LogError("Dropping room message at stream seq {Seq}: rejected by Postgres ({SqlState})", row.Seq, single.SqlState);
                    await row.Msg.AckTerminateAsync(cancellationToken: ct);
                    continue;
                }
                await row.Msg.AckAsync(cancellationToken: ct);
            }
            return;
        }
        catch (PostgresException ex) when (IsPermanent(ex))
        {
            logger.LogError("Dropping room message at stream seq {Seq}: rejected by Postgres ({SqlState})", rows[0].Seq, ex.SqlState);
            await rows[0].Msg.AckTerminateAsync(cancellationToken: ct);
            return;
        }

        foreach (var row in rows)
            await row.Msg.AckAsync(cancellationToken: ct);
    }

    internal static RoomMessage? TryParse(byte[]? data)
    {
        if (data is not { Length: > 0 })
            return null;

        RoomMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(data, DotwireJsonContext.Default.RoomMessage);
        }
        catch (JsonException)
        {
            return null;
        }

        if (message is null || message.RoomId == Guid.Empty || !Dotwire.Auth.IdValidation.IsValid(message.SenderId) ||
            string.IsNullOrEmpty(message.KeyId) || message.Content is not { Length: > 0 })
            return null;

        return message;
    }

    internal static bool IsPermanent(PostgresException ex) =>
        ex.SqlState.StartsWith("22", StringComparison.Ordinal) || ex.SqlState.StartsWith("23", StringComparison.Ordinal);

    private async Task InsertAsync(List<(RoomMessage Message, long Seq)> rows, CancellationToken ct)
    {
        var sql = new StringBuilder(
            "INSERT INTO messages (room_id, time, seq, sender_id, key_id, content) VALUES ");
        await using var conn = await writeDataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };

        for (var i = 0; i < rows.Count; i++)
        {
            var (message, seq) = rows[i];
            var time = message.Time.Year >= 2020 ? message.Time.UtcDateTime : DateTime.UtcNow;

            var p = i * 6;
            if (i > 0) sql.Append(", ");
            sql.Append($"(${p + 1}, ${p + 2}, ${p + 3}, ${p + 4}, ${p + 5}, ${p + 6})");
            cmd.Parameters.AddWithValue(message.RoomId);
            cmd.Parameters.AddWithValue(time);
            cmd.Parameters.AddWithValue(seq);
            cmd.Parameters.AddWithValue(message.SenderId);
            cmd.Parameters.AddWithValue(message.KeyId);
            cmd.Parameters.AddWithValue(message.Content);
        }

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
