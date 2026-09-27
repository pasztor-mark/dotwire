using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Data;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Npgsql;

namespace Dotwire.Nats;

/// <summary>
/// Single-writer audit hash chain (spec §3.6). Every <c>All</c>/<c>Api</c> node runs this
/// service; a Postgres session advisory lock elects exactly one writer at a time, so all
/// nodes can compete without racing the chain. The winner drains the durable
/// <c>audit-writer</c> pull consumer in batches, chains <c>hash = SHA256(prev_hash ‖
/// canonical)</c> per row (<see cref="AuditCanonicalForm"/>), and inserts each batch in one
/// transaction before acking - so a crash before ack simply redelivers, and the
/// `stream_seq &lt;= tail.stream_seq` prefix check on redelivery makes that redelivery a
/// no-op rather than a duplicate row.
/// </summary>
public sealed class AuditWriterService(
    INatsJSContext js,
    IOptions<NatsOptions> natsOptions,
    [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
    ILogger<AuditWriterService> logger) : BackgroundService
{
    // Distinct from MigrationRunner's 0x646F_7477_6972_65 ("dotwire") migration lock key:
    // that one is held briefly during startup; this one is held for the process lifetime
    // by whichever node wins it, so the two must never collide.
    private const long AdvisoryLockKey = 0x6175_6469_745F_7772;

    private DateOnly? _latestCheckpointDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = natsOptions.Value;
        if (!options.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            NpgsqlConnection? conn = null;
            try
            {
                conn = await AcquireLockedConnectionAsync(options, stoppingToken);
                await RunWhileLockedAsync(conn, options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A dropped connection here releases the session-scoped advisory lock on
                // the server side; looping back to AcquireLockedConnectionAsync opens a
                // fresh session and re-enters the contest for the lock.
                logger.LogError(ex, "Audit writer loop error, retrying: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            finally
            {
                if (conn is not null)
                    await conn.DisposeAsync();
            }
        }
    }

    private async Task<NpgsqlConnection> AcquireLockedConnectionAsync(NatsOptions options, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var conn = await writeDataSource.OpenConnectionAsync(ct);
            bool acquired;
            await using (var cmd = new NpgsqlCommand($"SELECT pg_try_advisory_lock({AdvisoryLockKey})", conn))
            {
                acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            }

            if (acquired)
                return conn;

            await conn.DisposeAsync();
            await Task.Delay(TimeSpan.FromSeconds(options.AuditWriter.StandbyRetrySeconds), ct);
        }
    }

    private async Task RunWhileLockedAsync(NpgsqlConnection conn, NatsOptions options, CancellationToken ct)
    {
        var (tailId, tailHash, tailStreamSeq) = await LoadTailAsync(conn, ct);
        _latestCheckpointDay = await LoadLatestCheckpointDayAsync(conn, ct);

        var consumer = await GetConsumerWithRetryAsync(options, ct);
        var fetchOpts = new NatsJSFetchOpts
        {
            MaxMsgs = options.AuditWriter.BatchMaxMessages,
            Expires = TimeSpan.FromMilliseconds(options.AuditWriter.BatchLingerMs),
        };

        while (!ct.IsCancellationRequested)
        {
            var batch = new List<INatsJSMsg<byte[]>>(options.AuditWriter.BatchMaxMessages);
            await foreach (var msg in consumer.FetchAsync<byte[]>(fetchOpts, cancellationToken: ct))
                batch.Add(msg);

            if (batch.Count == 0)
                continue;

            var result = await InsertBatchAsync(conn, batch, tailHash, tailStreamSeq, ct);

            foreach (var msg in batch)
                await msg.AckAsync(cancellationToken: ct);

            if (result is null)
                continue; // every message in the batch was already committed by a prior run.

            (tailId, tailHash, tailStreamSeq) = (result.Value.TailId, result.Value.TailHash, result.Value.TailStreamSeq);
            await MaybeCheckpointAsync(conn, tailId, tailHash, ct);
        }
    }

    private static async Task<(long Id, byte[] Hash, long StreamSeq)> LoadTailAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, hash, stream_seq FROM audit_log ORDER BY id DESC LIMIT 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (reader.GetInt64(0), (byte[])reader.GetValue(1), reader.GetInt64(2));

        return (0, AuditCanonicalForm.GenesisHash(), 0);
    }

    private static async Task<DateOnly?> LoadLatestCheckpointDayAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT day FROM audit_checkpoints ORDER BY day DESC LIMIT 1", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime dt ? DateOnly.FromDateTime(dt) : null;
    }

    private async Task<(long TailId, byte[] TailHash, long TailStreamSeq)?> InsertBatchAsync(
        NpgsqlConnection conn,
        List<INatsJSMsg<byte[]>> batch,
        byte[] tailHash,
        long tailStreamSeq,
        CancellationToken ct)
    {
        var rows = new List<(AuditEvent Event, byte[] Hash, byte[] PrevHash, long StreamSeq)>();
        var prevHash = tailHash;

        foreach (var msg in batch)
        {
            var streamSeq = (long)msg.Metadata!.Value.Sequence.Stream;
            if (streamSeq <= tailStreamSeq)
                continue; // already committed by a previous run; batches commit atomically.

            var evt = JsonSerializer.Deserialize(msg.Data!, DotwireJsonContext.Default.AuditEvent);
            if (evt is null)
                continue;

            var canonical = AuditCanonicalForm.Compute(evt);
            var hash = AuditCanonicalForm.Hash(prevHash, canonical);
            rows.Add((evt, hash, prevHash, streamSeq));
            prevHash = hash;
        }

        if (rows.Count == 0)
            return null;

        await using var tx = await conn.BeginTransactionAsync(ct);
        long lastId = 0;
        foreach (var row in rows)
        {
            var time = AuditCanonicalForm.TruncateToMicroseconds(row.Event.Time);
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO audit_log
                    (event_type, room_id, message_seq, actor_id, subject_id, value, event_time, prev_hash, hash, stream_seq)
                VALUES
                    ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                RETURNING id
                """, conn, tx);
            cmd.Parameters.AddWithValue(row.Event.EventType);
            cmd.Parameters.AddWithValue((object?)row.Event.RoomId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(row.Event.MessageSeq.HasValue ? (object)(long)row.Event.MessageSeq.Value : DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.Event.ActorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.Event.SubjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.Event.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue(time);
            cmd.Parameters.AddWithValue(row.PrevHash);
            cmd.Parameters.AddWithValue(row.Hash);
            cmd.Parameters.AddWithValue(row.StreamSeq);
            lastId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        await tx.CommitAsync(ct);

        var last = rows[^1];
        return (lastId, last.Hash, last.StreamSeq);
    }

    private async Task MaybeCheckpointAsync(NpgsqlConnection conn, long tailId, byte[] tailHash, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        if (_latestCheckpointDay.HasValue && today <= _latestCheckpointDay.Value)
            return;

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO audit_checkpoints (day, last_id, hash) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING",
            conn);
        cmd.Parameters.AddWithValue(today.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue(tailId);
        cmd.Parameters.AddWithValue(tailHash);
        await cmd.ExecuteNonQueryAsync(ct);

        _latestCheckpointDay = today;
    }

    private async Task<INatsJSConsumer> GetConsumerWithRetryAsync(NatsOptions options, CancellationToken ct)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await js.GetConsumerAsync(options.AuditStream, options.AuditWriterConsumer, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                logger.LogWarning("Consumer {Consumer} not available yet (attempt {Attempt}/{Max}): {Message}",
                    options.AuditWriterConsumer, attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
