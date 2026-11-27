using System.Text.Json;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Data;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Npgsql;

namespace Dotwire.Nats;

public sealed class AccessLogWriterService(
    INatsJSContext js,
    IOptions<NatsOptions> natsOptions,
    [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
    DotwireMetrics metrics,
    ILogger<AccessLogWriterService> logger) : BackgroundService
{
    internal TimeSpan ConsumerRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = natsOptions.Value;
        if (!options.Enabled)
            return;

        var fetchOpts = new NatsJSFetchOpts
        {
            MaxMsgs = options.AccessLogWriter.BatchMaxMessages,
            Expires = TimeSpan.FromMilliseconds(options.AccessLogWriter.BatchLingerMs),
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await GetConsumerAsync(options, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var batch = new List<INatsJSMsg<byte[]>>(options.AccessLogWriter.BatchMaxMessages);
                    await foreach (var msg in consumer.FetchAsync<byte[]>(fetchOpts, cancellationToken: stoppingToken))
                        batch.Add(msg);

                    if (batch.Count == 0)
                        continue;

                    await InsertBatchAsync(batch, stoppingToken);
                    foreach (var msg in batch)
                        await msg.AckAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Access log writer loop error ({ErrorType}), retrying: {Error}", ex.GetType().Name, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task<INatsJSConsumer> GetConsumerAsync(NatsOptions options, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return await js.GetConsumerAsync(options.AccessLogStream, options.AccessLogWriterConsumer, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("Access log consumer {Consumer} not available yet ({ErrorType}): {Error}",
                    options.AccessLogWriterConsumer, ex.GetType().Name, ex.Message);
                await Task.Delay(ConsumerRetryDelay, ct);
            }
        }
    }

    internal async Task InsertBatchAsync(IReadOnlyList<INatsJSMsg<byte[]>> batch, CancellationToken ct)
    {
        var rows = new List<(AccessEvent Event, long StreamSeq)>(batch.Count);
        foreach (var msg in batch)
        {
            var streamSeq = (long)msg.Metadata!.Value.Sequence.Stream;
            var evt = ParseOrNull(msg.Data);
            if (evt is null)
            {
                logger.LogError("Malformed access event at stream seq {StreamSeq}", streamSeq);
                metrics.AccessLogMalformed();
                continue;
            }

            rows.Add((evt, streamSeq));
        }

        if (rows.Count == 0)
            return;

        await using var conn = await writeDataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlBatch(conn, tx);
        foreach (var (e, streamSeq) in rows)
        {
            var cmd = new NpgsqlBatchCommand(
                """
                INSERT INTO access_log
                    (time, stream_seq, event, room_id, user_id, actor_id, role, via, window_id, expires_at, reason, from_seq, to_seq, count)
                VALUES
                    ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                ON CONFLICT DO NOTHING
                """);
            cmd.Parameters.AddWithValue(e.Time.ToUniversalTime());
            cmd.Parameters.AddWithValue(streamSeq);
            cmd.Parameters.AddWithValue(e.Event);
            cmd.Parameters.AddWithValue(e.RoomId);
            cmd.Parameters.AddWithValue(e.UserId);
            cmd.Parameters.AddWithValue((object?)e.ActorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(e.Role);
            cmd.Parameters.AddWithValue(e.Via);
            cmd.Parameters.AddWithValue(e.WindowId.HasValue ? e.WindowId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(e.ExpiresAt.HasValue ? e.ExpiresAt.Value.ToUniversalTime() : DBNull.Value);
            cmd.Parameters.AddWithValue((object?)e.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue(e.FromSeq.HasValue ? (long)e.FromSeq.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(e.ToSeq.HasValue ? (long)e.ToSeq.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(e.Count.HasValue ? e.Count.Value : DBNull.Value);
            insert.BatchCommands.Add(cmd);
        }

        await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal static AccessEvent? ParseOrNull(byte[]? data)
    {
        AccessEvent? e;
        try
        {
            if (data is not { Length: > 0 })
                return null;
            e = JsonSerializer.Deserialize(data, DotwireJsonContext.Default.AccessEvent);
        }
        catch (JsonException)
        {
            return null;
        }

        if (e is null || e.Time == default || e.Event is null || e.UserId is null || e.Role is null || e.Via is null)
            return null;
        if (!IdValidation.IsValid(e.UserId) || (e.ActorId is not null && !IdValidation.IsValid(e.ActorId)))
            return null;
        if (e.Via is not (AccessEvents.Hub or AccessEvents.Sse or AccessEvents.Rest))
            return null;
        if (e.Role is not ("member" or "auditor" or "admin"))
            return null;
        if (e.Reason is not null and not (AccessEvents.Left or AccessEvents.Disconnected or AccessEvents.Revoked or AccessEvents.Error))
            return null;

        return e.Event switch
        {
            AccessEvents.Opened when e.WindowId is not null && e.ExpiresAt is not null => e,
            AccessEvents.Closed when e.WindowId is not null && e.Reason is not null => e,
            AccessEvents.HistoryRead when e.Count is not null => e,
            _ => null,
        };
    }
}
