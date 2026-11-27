using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Dotwire.Api;

/// <summary>
/// Read-only, auditor-only access to the hash-chained audit log (spec §3.7). Every read
/// is itself an audited event (`audit.read`) - reading the log is a privileged action too.
/// </summary>
public static class Audit
{
    public static void MapAuditEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/audit")
            .RequireAuthorization()
            .AddEndpointFilter(new RequireRoleFilter("auditor"))
            .RequireRateLimiting("read");

        group.MapGet("/", async (
            long? afterId,
            int? limit,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            AuditPublisher auditPublisher,
            HttpContext http) =>
        {
            var after = afterId ?? 0;
            var pageSize = Math.Clamp(limit ?? 100, 1, 500);
            var ct = http.RequestAborted;

            var entries = new List<AuditEntry>(pageSize);
            await using (var cmd = readDataSource.CreateCommand(
                "SELECT id, event_type, room_id, message_seq, actor_id, subject_id, value, event_time, hash " +
                "FROM audit_log WHERE id > $1 ORDER BY id ASC LIMIT $2"))
            {
                cmd.Parameters.AddWithValue(after);
                cmd.Parameters.AddWithValue(pageSize + 1);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    entries.Add(ReadEntry(reader));
            }

            var hasMore = entries.Count > pageSize;
            if (hasMore)
                entries.RemoveAt(entries.Count - 1);

            if (!await TryEmitAuditReadAsync(auditPublisher, http, ct))
                return ApiResults.AuditUnavailable();

            return Results.Ok(new AuditPageResponse(entries.ToArray(), hasMore));
        });

        group.MapGet("/access", async (
            string? from,
            string? to,
            string? roomId,
            string? userId,
            int? limit,
            string? cursor,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            AuditPublisher auditPublisher,
            IOptions<AuthOptions> authOptions,
            HttpContext http) =>
        {
            var ct = http.RequestAborted;
            if (!TryParseTime(from, out var fromTime))
                return ApiResults.InvalidRequest("from is required and must be ISO 8601");

            var toTime = DateTimeOffset.UtcNow;
            if (to is not null && !TryParseTime(to, out toTime))
                return ApiResults.InvalidRequest("to must be ISO 8601");
            if (toTime < fromTime)
                return ApiResults.InvalidRequest("to must not be before from");

            Guid? room = null;
            if (roomId is not null)
            {
                if (!Guid.TryParse(roomId, out var parsedRoom))
                    return ApiResults.InvalidRequest("roomId must be a uuid");
                room = parsedRoom;
            }

            if (userId is not null && !IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            (DateTimeOffset Start, long Seq)? after = null;
            if (cursor is not null)
            {
                if (!TryParseCursor(cursor, out var parsedCursor))
                    return ApiResults.InvalidRequest("cursor is invalid");
                after = parsedCursor;
            }

            var pageSize = Math.Clamp(limit ?? 100, 1, 500);
            var auth = authOptions.Value;
            var lookback = fromTime - TimeSpan.FromMinutes(auth.MaxTokenLifetimeMinutes) - TimeSpan.FromSeconds(auth.ClockSkewSeconds);

            var liveFilter = new StringBuilder();
            var historyFilter = new StringBuilder();
            if (room is not null)
            {
                liveFilter.Append(" AND o.room_id = @room");
                historyFilter.Append(" AND room_id = @room");
            }
            if (userId is not null)
            {
                liveFilter.Append(" AND o.user_id = @user");
                historyFilter.Append(" AND user_id = @user");
            }

            var sql =
                "SELECT kind, start, stream_seq, room_id, user_id, actor_id, role, via, window_id, end_time, end_reason, expires_at, from_seq, to_seq, cnt FROM (" +
                "SELECT 'live' AS kind, o.time AS start, o.stream_seq, o.room_id, o.user_id, o.actor_id, o.role, o.via, o.window_id, " +
                "c.time AS end_time, c.reason AS end_reason, o.expires_at, NULL::bigint AS from_seq, NULL::bigint AS to_seq, NULL::integer AS cnt " +
                "FROM access_log o LEFT JOIN LATERAL (" +
                "SELECT c.time, c.reason FROM access_log c " +
                "WHERE c.room_id = o.room_id AND c.window_id = o.window_id AND c.event = 'live.closed' " +
                "AND c.time >= o.time AND c.time <= o.expires_at + (@skew * interval '1 second') " +
                "ORDER BY c.time, c.stream_seq LIMIT 1) c ON true " +
                "WHERE o.event = 'live.opened' AND o.time >= @lookback AND o.time <= @to AND o.expires_at >= @from" + liveFilter +
                " AND (c.time IS NULL OR c.time >= @from) " +
                "UNION ALL " +
                "SELECT 'history', time, stream_seq, room_id, user_id, actor_id, role, via, NULL::uuid, NULL::timestamptz, NULL::text, NULL::timestamptz, from_seq, to_seq, count " +
                "FROM access_log WHERE event = 'history.read' AND time >= @from AND time <= @to" + historyFilter +
                ") x" +
                (after is null ? "" : " WHERE (start, stream_seq) > (@cursorStart, @cursorSeq)") +
                " ORDER BY start, stream_seq LIMIT @limit";

            var entries = new List<AccessLogEntry>(pageSize + 1);
            var seqs = new List<long>(pageSize + 1);
            await using (var cmd = readDataSource.CreateCommand(sql))
            {
                cmd.Parameters.AddWithValue("from", fromTime);
                cmd.Parameters.AddWithValue("to", toTime);
                cmd.Parameters.AddWithValue("lookback", lookback);
                cmd.Parameters.AddWithValue("skew", auth.ClockSkewSeconds);
                cmd.Parameters.AddWithValue("limit", pageSize + 1);
                if (room is not null)
                    cmd.Parameters.AddWithValue("room", room.Value);
                if (userId is not null)
                    cmd.Parameters.AddWithValue("user", userId);
                if (after is not null)
                {
                    cmd.Parameters.AddWithValue("cursorStart", after.Value.Start);
                    cmd.Parameters.AddWithValue("cursorSeq", after.Value.Seq);
                }

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    seqs.Add(reader.GetInt64(2));
                    entries.Add(new AccessLogEntry(
                        reader.GetString(0),
                        reader.GetGuid(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetFieldValue<DateTimeOffset>(1),
                        reader.IsDBNull(8) ? null : reader.GetGuid(8),
                        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                        reader.IsDBNull(12) ? null : (ulong)reader.GetInt64(12),
                        reader.IsDBNull(13) ? null : (ulong)reader.GetInt64(13),
                        reader.IsDBNull(14) ? null : reader.GetInt32(14)));
                }
            }

            string? next = null;
            if (entries.Count > pageSize)
            {
                entries.RemoveAt(entries.Count - 1);
                seqs.RemoveAt(seqs.Count - 1);
                var micros = (entries[^1].Start - DateTimeOffset.UnixEpoch).Ticks / 10;
                next = $"{micros}.{seqs[^1]}";
            }

            if (!await TryEmitAuditReadAsync(auditPublisher, http, ct, "access_log.read", room, userId))
                return ApiResults.AuditUnavailable();

            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new AccessLogPageResponse(entries.ToArray(), next));
        });

        group.MapGet("/verify", async (
            bool? full,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            AuditPublisher auditPublisher,
            DotwireMetrics metrics,
            HttpContext http) =>
        {
            var ct = http.RequestAborted;
            var runFull = full ?? false;

            if (runFull && !FullVerifyGate.Wait(0))
            {
                metrics.Rejection("rest", "busy", "audit_verify");
                return ApiResults.RateLimited(5);
            }

            try
            {
                return await VerifyAsync(runFull, readDataSource, auditPublisher, http, ct);
            }
            finally
            {
                if (runFull)
                    FullVerifyGate.Release();
            }
        });

        group.MapGet("/checkpoints", async (
            int? limit,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            AuditPublisher auditPublisher,
            HttpContext http) =>
        {
            var pageSize = Math.Clamp(limit ?? 30, 1, 365);
            var ct = http.RequestAborted;

            var checkpoints = new List<AuditCheckpointEntry>(pageSize);
            await using (var cmd = readDataSource.CreateCommand(
                "SELECT day, last_id, hash, created_at FROM audit_checkpoints ORDER BY day DESC LIMIT $1"))
            {
                cmd.Parameters.AddWithValue(pageSize);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    checkpoints.Add(new AuditCheckpointEntry(
                        DateOnly.FromDateTime(reader.GetDateTime(0)),
                        reader.GetInt64(1),
                        Convert.ToHexStringLower((byte[])reader.GetValue(2)),
                        reader.GetFieldValue<DateTimeOffset>(3)));
                }
            }

            if (!await TryEmitAuditReadAsync(auditPublisher, http, ct))
                return ApiResults.AuditUnavailable();

            return Results.Ok(new AuditCheckpointsResponse(checkpoints.ToArray()));
        });
    }

    internal static readonly SemaphoreSlim FullVerifyGate = new(1, 1);

    internal static bool AnchorMatches(byte[] checkpointHash, byte[]? auditRowHash) =>
        auditRowHash is not null && auditRowHash.AsSpan().SequenceEqual(checkpointHash);

    private static async Task<IResult> VerifyAsync(
        bool runFull, NpgsqlDataSource readDataSource, AuditPublisher auditPublisher, HttpContext http, CancellationToken ct)
    {
        long fromId = 0;
        byte[] prevHash = AuditCanonicalForm.GenesisHash();
        DateOnly? anchor = null;

        if (!runFull)
        {
            await using (var cmd = readDataSource.CreateCommand(
                "SELECT day, last_id, hash FROM audit_checkpoints WHERE day <= (now() AT TIME ZONE 'UTC')::date ORDER BY day DESC LIMIT 1"))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    anchor = DateOnly.FromDateTime(reader.GetDateTime(0));
                    fromId = reader.GetInt64(1);
                    prevHash = (byte[])reader.GetValue(2);
                }
            }

            if (anchor.HasValue)
            {
                await using var anchorCmd = readDataSource.CreateCommand("SELECT hash FROM audit_log WHERE id = $1");
                anchorCmd.Parameters.AddWithValue(fromId);
                var rowHash = await anchorCmd.ExecuteScalarAsync(ct) as byte[];
                if (!AnchorMatches(prevHash, rowHash))
                {
                    if (!await TryEmitAuditReadAsync(auditPublisher, http, ct))
                        return ApiResults.AuditUnavailable();
                    return Results.Ok(new AuditVerificationResponse(false, fromId, fromId, 0, fromId, anchor));
                }
            }
        }

        long checkedCount = 0;
        long lastId = fromId;
        long? firstInvalidId = null;

        await using (var cmd = readDataSource.CreateCommand(
            "SELECT id, event_type, room_id, message_seq, actor_id, subject_id, value, event_time, prev_hash, hash " +
            "FROM audit_log WHERE id > $1 ORDER BY id ASC"))
        {
            cmd.Parameters.AddWithValue(fromId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var evt = new AuditEvent(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetFieldValue<DateTimeOffset>(7));
                var storedPrevHash = (byte[])reader.GetValue(8);
                var storedHash = (byte[])reader.GetValue(9);

                checkedCount++;
                lastId = id;

                if (firstInvalidId is not null)
                    continue; // keep scanning `toId` to the real tail, but stop judging.

                if (!storedPrevHash.AsSpan().SequenceEqual(prevHash))
                {
                    firstInvalidId = id;
                    continue;
                }

                var recomputed = AuditCanonicalForm.Hash(prevHash, AuditCanonicalForm.Compute(evt));
                if (!recomputed.AsSpan().SequenceEqual(storedHash))
                {
                    firstInvalidId = id;
                    continue;
                }

                prevHash = storedHash;
            }
        }

        if (!await TryEmitAuditReadAsync(auditPublisher, http, ct))
            return ApiResults.AuditUnavailable();

        return Results.Ok(new AuditVerificationResponse(
            Ok: firstInvalidId is null,
            FromId: fromId,
            ToId: lastId,
            Checked: checkedCount,
            FirstInvalidId: firstInvalidId,
            Anchor: anchor));
    }

    private const long MaxCursorMicros = 253402300799999999L;

    private static bool TryParseTime(string? value, out DateTimeOffset time)
    {
        time = default;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
            return false;
        return time.Year is >= 1971 and <= 9998;
    }

    private static bool TryParseCursor(string cursor, out (DateTimeOffset Start, long Seq) parsed)
    {
        parsed = default;
        var parts = cursor.Split('.');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var micros)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            || micros > MaxCursorMicros)
            return false;
        parsed = (DateTimeOffset.UnixEpoch.AddTicks(micros * 10), seq);
        return true;
    }

    private static AuditEntry ReadEntry(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        Convert.ToHexStringLower((byte[])reader.GetValue(8)));

    /// <summary>Emits `audit.read` with the caller as actor; returns false on publish failure (caller returns 503).</summary>
    private static async Task<bool> TryEmitAuditReadAsync(AuditPublisher auditPublisher, HttpContext http, CancellationToken ct,
        string eventType = "audit.read", Guid? roomId = null, string? subjectId = null)
    {
        var actorId = http.User.FindFirstValue("sub")!;
        var evt = new AuditEvent(eventType, roomId, null, actorId, subjectId, null, DateTimeOffset.UtcNow);
        try
        {
            await auditPublisher.PublishAsync(evt, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Dotwire.Api.Audit")
                .LogError("Audit publish failed for {EventType} by {ActorId}: {Error}", eventType, actorId, ex.Message);
            return false;
        }
    }
}
