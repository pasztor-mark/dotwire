using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using NpgsqlTypes;

namespace Dotwire.Nats;

public sealed class ErasureWorkerService(
    INatsJSContext js,
    INatsConnection nats,
    IOptions<NatsOptions> natsOptions,
    IOptions<ComplianceOptions> complianceOptions,
    IOptions<DotwireOptions> dotwireOptions,
    IOptions<AuthOptions> authOptions,
    [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
    [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
    MessageCipher cipher,
    ILsnSource lsnSource,
    AuditPublisher auditPublisher,
    ErasureNoticePublisher noticePublisher,
    ILogger<ErasureWorkerService> logger) : BackgroundService
{
    public const string WakeSubject = "dotwire.erasure.wake";

    private const int PageSize = 200;
    private const int MaxSeqsPerEvent = 500;
    private const int JetStreamNoMessageFound = 10037;
    private const int JetStreamSequenceNotFound = 10043;
    private static readonly TimeSpan ReplicaPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WriterPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly HashSet<long> _revocationDone = [];
    private readonly Dictionary<long, List<ErasureNotice>> _pendingNotices = [];
    private readonly Dictionary<long, List<(Guid RoomId, ulong Seq)>> _pendingStreamDeletes = [];
    private static readonly TimeSpan WriterWarnInterval = TimeSpan.FromSeconds(60);

    internal static TimeSpan RevocationWindow(DotwireOptions dotwire, AuthOptions auth)
    {
        var seconds = dotwire.MembershipSweepSeconds > 0
            ? dotwire.MembershipSweepSeconds
            : auth.MaxTokenLifetimeMinutes > 0 ? auth.MaxTokenLifetimeMinutes * 60 : 3600;
        return TimeSpan.FromSeconds(Math.Max(Math.Max(seconds, dotwire.AuthCacheSeconds), 5));
    }

    internal static bool MayStillBeInStream(DateTimeOffset time, int maxAgeHours, DateTimeOffset now) =>
        maxAgeHours <= 0 || time > now.AddHours(-maxAgeHours);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NpgsqlConnection? conn = null;
            try
            {
                conn = await AcquireLockedConnectionAsync(stoppingToken);
                await RunWhileLockedAsync(conn, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Erasure worker loop error ({ErrorType}), retrying: {Error}", ex.GetType().Name, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            finally
            {
                if (conn is not null)
                    await conn.DisposeAsync();
            }
        }
    }

    private async Task<NpgsqlConnection> AcquireLockedConnectionAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var conn = await writeDataSource.OpenConnectionAsync(ct);
            bool acquired;
            await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended('dotwire.erasure_worker', 0))", conn))
                acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;

            if (acquired)
                return conn;

            await conn.DisposeAsync();
            await Task.Delay(TimeSpan.FromSeconds(natsOptions.Value.AuditWriter.StandbyRetrySeconds), ct);
        }
    }

    private async Task RunWhileLockedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var recheck = TimeSpan.FromMinutes(Math.Max(1, complianceOptions.Value.Erasure.RecheckMinutes));
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        INatsSub<byte[]>? sub = null;
        Task? pump = null;
        try
        {
            if (natsOptions.Value.Enabled)
            {
                sub = await nats.SubscribeCoreAsync<byte[]>(WakeSubject, cancellationToken: ct);
                var messages = sub.Msgs;
                pump = Task.Run(async () =>
                {
                    await foreach (var _ in messages.ReadAllAsync(ct))
                        wake.Writer.TryWrite(true);
                }, ct);
            }

            while (!ct.IsCancellationRequested)
            {
                var nextDue = await PassAsync(conn, ct);
                var lastPass = TimeProvider.System.GetTimestamp();
                var dueAt = nextDue is { } d ? TimeProvider.System.GetTimestamp() + (long)(d.TotalSeconds * TimeProvider.System.TimestampFrequency) : (long?)null;

                while (true)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var slice = KeepAlive;
                    if (dueAt is { } due)
                    {
                        var untilDue = TimeProvider.System.GetElapsedTime(TimeProvider.System.GetTimestamp(), due);
                        slice = untilDue <= TimeSpan.Zero ? TimeSpan.Zero : untilDue < slice ? untilDue : slice;
                    }

                    cts.CancelAfter(slice);
                    try
                    {
                        await wake.Reader.ReadAsync(cts.Token);
                        break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (TimeProvider.System.GetElapsedTime(lastPass) >= recheck
                            || (dueAt is { } reached && TimeProvider.System.GetTimestamp() >= reached))
                            break;
                        await using var ping = new NpgsqlCommand("SELECT 1", conn);
                        await ping.ExecuteScalarAsync(ct);
                    }
                }
            }
        }
        finally
        {
            if (sub is not null)
                await sub.DisposeAsync();
            if (pump is not null)
            {
                try
                {
                    await pump;
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private async Task<TimeSpan?> PassAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await FlushNoticesAsync(ct);

        var window = RevocationWindow(dotwireOptions.Value, authOptions.Value);
        var open = new List<(long Id, string UserId, string Mode, double WindowSeconds)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT e.id, e.user_id, e.mode, " +
            "GREATEST(0, EXTRACT(EPOCH FROM (e.requested_at + make_interval(secs => $1) - now())))::float8 " +
            "FROM erasures e WHERE NOT EXISTS (SELECT 1 FROM erasure_completions c WHERE c.erasure_id = e.id) ORDER BY e.id", conn))
        {
            cmd.Parameters.AddWithValue(window.TotalSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                open.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));
        }

        double? earliest = null;
        foreach (var (id, userId, wireMode, windowSeconds) in open)
        {
            try
            {
                if (!ErasureModes.TryParse(wireMode, out var mode))
                    continue;
                if (windowSeconds > 0 && !_revocationDone.Contains(id))
                {
                    earliest = earliest is { } e ? Math.Min(e, windowSeconds) : windowSeconds;
                    continue;
                }

                await ProcessAsync(conn, id, userId, mode, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!await IsLockAliveAsync(conn, ct))
                    throw;
                logger.LogError("Erasure {ErasureId} pass failed ({ErrorType}), retrying on the next wake: {Error}", id, ex.GetType().Name, ex.Message);
            }
        }

        return earliest is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static async Task<bool> IsLockAliveAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (conn.State != System.Data.ConnectionState.Open)
            return false;
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ProcessAsync(NpgsqlConnection conn, long id, string userId, ErasureMode mode, CancellationToken ct)
    {
        if (!_revocationDone.Contains(id))
        {
            await WaitForReplicaAsync(id, await RevocationLsnAsync(conn, id, ct), ct);
            _revocationDone.Add(id);
        }

        await WaitForWriterAsync(id, ct);
        await WaitForReplicaAsync(id, await lsnSource.PrimaryLsnAsync(ct), ct);

        var options = natsOptions.Value;
        var stream = options.Enabled ? await js.GetStreamAsync(options.RoomsStream, cancellationToken: ct) : null;
        await RetryStreamDeletesAsync(id, stream, ct);

        await ProcessPagesAsync(id, userId, mode, stream, ct);

        await WaitForReplicaAsync(id, await lsnSource.PrimaryLsnAsync(ct), ct);
        if (await CountMessagesAsync(userId, ct) > 0 || _pendingStreamDeletes.TryGetValue(id, out var stuck) && stuck.Count > 0)
            return;

        try
        {
            await auditPublisher.PublishAsync(
                new AuditEvent("user.erased", null, null, null, userId, (long)(int)mode, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Audit publish failed for erasure {ErasureId}; it stays open and retries on the next wake: {Error}", id, ex.Message);
            return;
        }

        await using (var insert = new NpgsqlCommand("INSERT INTO erasure_completions (erasure_id) VALUES ($1) ON CONFLICT DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue(id);
            await insert.ExecuteNonQueryAsync(ct);
        }

        _revocationDone.Remove(id);
        if (noticePublisher.IsEnabled)
        {
            Enqueue(id, new ErasureNotice("user.erased", DateTimeOffset.UtcNow,
                UserId: userId, Mode: ErasureModes.ToWire(mode), ErasureId: id, Status: "completed"));
            await FlushNoticesAsync(ct);
        }
    }

    private static async Task<ulong> RevocationLsnAsync(NpgsqlConnection conn, long id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var read = new NpgsqlCommand("SELECT lsn::text FROM erasure_revocations WHERE erasure_id = $1", conn))
            {
                read.Parameters.AddWithValue(id);
                if (await read.ExecuteScalarAsync(ct) is string lsn)
                    return (ulong)NpgsqlLogSequenceNumber.Parse(lsn);
            }

            await using var insert = new NpgsqlCommand(
                "INSERT INTO erasure_revocations (erasure_id, lsn) VALUES ($1, pg_current_wal_lsn()) ON CONFLICT DO NOTHING", conn);
            insert.Parameters.AddWithValue(id);
            await insert.ExecuteNonQueryAsync(ct);
        }

        throw new InvalidOperationException($"No revocation LSN for erasure {id}");
    }

    private Task WaitForReplicaAsync(long erasureId, ulong target, CancellationToken ct) =>
        ReplicaCatchUp.WaitForReplicaAsync(lsnSource, target, ReplicaPoll, ct,
            (waited, replayed) => logger.LogWarning(
                "Erasure {ErasureId} still waiting for the read replica after {WaitSeconds}s: replayed LSN {ReplayedLsn}, target LSN {TargetLsn}",
                erasureId, (int)waited.TotalSeconds, new NpgsqlLogSequenceNumber(replayed).ToString(), new NpgsqlLogSequenceNumber(target).ToString()),
            WriterWarnInterval);

    private async Task WaitForWriterAsync(long erasureId, CancellationToken ct)
    {
        var options = natsOptions.Value;
        if (!options.Enabled)
            return;

        var stream = await js.GetStreamAsync(options.RoomsStream, cancellationToken: ct);
        var lastSeq = stream.Info.State.LastSeq;
        var consumer = await js.GetConsumerAsync(options.RoomsStream, options.PostgresWriterConsumer, ct);
        var waitStart = TimeProvider.System.GetTimestamp();
        var lastWarn = waitStart;
        while (true)
        {
            var info = consumer.Info;
            if (info.AckFloor.StreamSeq >= lastSeq || (info.NumPending == 0 && info.NumAckPending == 0))
                return;

            if (TimeProvider.System.GetElapsedTime(lastWarn) >= WriterWarnInterval)
            {
                lastWarn = TimeProvider.System.GetTimestamp();
                logger.LogWarning(
                    "Erasure {ErasureId} still waiting for the {Consumer} consumer after {WaitSeconds}s: ack floor {AckFloor}, stream last seq {LastSeq}, pending {Pending}, ack pending {AckPending}",
                    erasureId, options.PostgresWriterConsumer, (int)TimeProvider.System.GetElapsedTime(waitStart).TotalSeconds,
                    info.AckFloor.StreamSeq, lastSeq, info.NumPending, info.NumAckPending);
            }

            await Task.Delay(WriterPoll, ct);
            await consumer.RefreshAsync(ct);
        }
    }

    private sealed record Row(Guid RoomId, long Seq, DateTimeOffset Time, string KeyId, byte[] Content);

    private async Task ProcessPagesAsync(long id, string userId, ErasureMode mode, INatsJSStream? stream, CancellationToken ct)
    {
        var compliance = complianceOptions.Value;
        var wireMode = ErasureModes.ToWire(mode);
        Row? last = null;
        await using var write = await writeDataSource.OpenConnectionAsync(ct);

        while (true)
        {
            var page = await ReadPageAsync(userId, last, ct);
            if (page.Count == 0)
                return;

            last = page[^1];
            var processed = new List<Row>();
            foreach (var row in page)
            {
                if (await ApplyAsync(write, row, userId, mode, compliance, ct) >= 1)
                    processed.Add(row);
            }

            if (processed.Count > 0)
            {
                if (stream is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    var maxAgeHours = natsOptions.Value.RoomsMaxAgeHours;
                    foreach (var row in processed.Where(r => MayStillBeInStream(r.Time, maxAgeHours, now)))
                        await DeleteFromStreamAsync(id, stream, row.RoomId, (ulong)row.Seq, ct);
                }

                if (noticePublisher.IsEnabled)
                {
                    foreach (var chunk in processed.Chunk(ErasureNoticePublisher.MaxMessagesPerNotice))
                    {
                        Enqueue(id, new ErasureNotice("user.erased", DateTimeOffset.UtcNow,
                            UserId: userId, Mode: wireMode, ErasureId: id, Status: "partial",
                            Messages: chunk.Select(r => new ErasedMessageRef(r.RoomId, (ulong)r.Seq)).ToArray()));
                    }

                    await FlushNoticesAsync(ct);
                }

                if (natsOptions.Value.Enabled)
                    await PublishLiveAsync(processed, wireMode, ct);
            }

            if (page.Count < PageSize)
                return;
        }
    }

    private async Task<List<Row>> ReadPageAsync(string userId, Row? after, CancellationToken ct)
    {
        var rows = new List<Row>(PageSize);
        await using var cmd = readDataSource.CreateCommand(
            "SELECT room_id, seq, time, key_id, content FROM messages WHERE sender_id = $1 " +
            "AND ($2::timestamptz IS NULL OR (time, room_id, seq) > ($2, $3, $4)) " +
            "ORDER BY time, room_id, seq LIMIT 200");
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = after is null ? DBNull.Value : after.Time });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = after is null ? DBNull.Value : after.RoomId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = after is null ? DBNull.Value : after.Seq });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Row(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetFieldValue<byte[]>(4)));
        }

        return rows;
    }

    private async Task<int> ApplyAsync(
        NpgsqlConnection write, Row row, string userId, ErasureMode mode, ComplianceOptions compliance, CancellationToken ct)
    {
        if (mode == ErasureMode.Delete)
            return await RedactAsync(write, row, userId, compliance, ct);

        byte[]? plain = null;
        try
        {
            try
            {
                plain = cipher.Decrypt(row.KeyId, row.Content, row.RoomId, userId);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
            {
                logger.LogWarning(
                    "Message {RoomId}/{Seq} cannot be decrypted ({ErrorType}); deleting it instead of anonymizing",
                    row.RoomId, row.Seq, ex.GetType().Name);
                return await RedactAsync(write, row, userId, compliance, ct);
            }

            var content = cipher.Encrypt(plain, row.RoomId, IdValidation.ReservedErasedId);
            await using var cmd = new NpgsqlCommand("SELECT reattribute_message($1, $2, $3, $4, $5, $6, $7)", write);
            cmd.Parameters.AddWithValue(row.RoomId);
            cmd.Parameters.AddWithValue(row.Seq);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = userId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = cipher.ActiveKeyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = content });
            cmd.Parameters.AddWithValue(compliance.MinimumKeepDays);
            cmd.Parameters.AddWithValue(compliance.AllowChanges);
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static async Task<int> RedactAsync(NpgsqlConnection write, Row row, string userId, ComplianceOptions compliance, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT redact_message_checked($1, $2, $3, $4, $5, $6)", write);
        cmd.Parameters.AddWithValue(row.RoomId);
        cmd.Parameters.AddWithValue(row.Seq);
        cmd.Parameters.AddWithValue(compliance.MinimumKeepDays);
        cmd.Parameters.AddWithValue(compliance.AllowChanges);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = userId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = row.Time });
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task DeleteFromStreamAsync(long id, INatsJSStream stream, Guid roomId, ulong seq, CancellationToken ct)
    {
        try
        {
            var stored = await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, ct);
            if (stored.Message.Subject == $"{natsOptions.Value.RoomSubjectPrefix}{roomId}")
                await stream.DeleteMessageAsync(new StreamMsgDeleteRequest { Seq = seq, NoErase = false }, ct);
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode is JetStreamNoMessageFound or JetStreamSequenceNotFound)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("JetStream delete failed for room {RoomId} seq {Seq}, retrying on the next wake: {Error}", roomId, seq, ex.Message);
            if (!_pendingStreamDeletes.TryGetValue(id, out var pending))
                _pendingStreamDeletes[id] = pending = [];
            if (!pending.Contains((roomId, seq)))
                pending.Add((roomId, seq));
        }
    }

    private async Task RetryStreamDeletesAsync(long id, INatsJSStream? stream, CancellationToken ct)
    {
        if (stream is null || !_pendingStreamDeletes.TryGetValue(id, out var pending) || pending.Count == 0)
            return;

        var retry = pending.ToArray();
        _pendingStreamDeletes.Remove(id);
        foreach (var (roomId, seq) in retry)
            await DeleteFromStreamAsync(id, stream, roomId, seq, ct);
    }

    private async Task<long> CountMessagesAsync(string userId, CancellationToken ct)
    {
        await using var cmd = readDataSource.CreateCommand("SELECT count(*) FROM messages WHERE sender_id = $1");
        cmd.Parameters.AddWithValue(userId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private void Enqueue(long id, ErasureNotice notice)
    {
        if (!_pendingNotices.TryGetValue(id, out var list))
            _pendingNotices[id] = list = [];
        list.Add(notice);
    }

    private async Task FlushNoticesAsync(CancellationToken ct)
    {
        foreach (var id in _pendingNotices.Keys.ToArray())
        {
            var list = _pendingNotices[id];
            while (list.Count > 0)
            {
                try
                {
                    await noticePublisher.PublishAsync(list[0], ct);
                    list.RemoveAt(0);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Erasure notice publish failed for erasure {ErasureId}, retrying on the next wake: {Error}", id, ex.Message);
                    break;
                }
            }

            if (list.Count == 0)
                _pendingNotices.Remove(id);
        }
    }

    private async Task PublishLiveAsync(List<Row> processed, string wireMode, CancellationToken ct)
    {
        foreach (var room in processed.GroupBy(r => r.RoomId))
        {
            foreach (var chunk in room.Chunk(MaxSeqsPerEvent))
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    new MessagesErased(room.Key, wireMode, chunk.Select(r => (ulong)r.Seq).ToArray()),
                    DotwireJsonContext.Default.MessagesErased);
                try
                {
                    await nats.PublishAsync<byte[]>($"room.{room.Key}.ephemeral.erased", payload, cancellationToken: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("MessagesErased broadcast failed for room {RoomId}: {Error}", room.Key, ex.Message);
                }
            }
        }
    }
}
