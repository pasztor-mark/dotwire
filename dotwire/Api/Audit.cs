using System.Security.Claims;
using Dotwire.Auth;
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
            .AddEndpointFilter<RequireAuditorFilter>()
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

        group.MapGet("/verify", async (
            bool? full,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            AuditPublisher auditPublisher,
            HttpContext http) =>
        {
            var ct = http.RequestAborted;
            var runFull = full ?? false;

            long fromId;
            byte[] prevHash;
            DateOnly? anchor = null;

            if (!runFull)
            {
                await using var cmd = readDataSource.CreateCommand(
                    "SELECT day, last_id, hash FROM audit_checkpoints ORDER BY day DESC LIMIT 1");
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    anchor = DateOnly.FromDateTime(reader.GetDateTime(0));
                    fromId = reader.GetInt64(1);
                    prevHash = (byte[])reader.GetValue(2);
                }
                else
                {
                    fromId = 0;
                    prevHash = AuditCanonicalForm.GenesisHash();
                }
            }
            else
            {
                fromId = 0;
                prevHash = AuditCanonicalForm.GenesisHash();
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
    private static async Task<bool> TryEmitAuditReadAsync(AuditPublisher auditPublisher, HttpContext http, CancellationToken ct)
    {
        var actorId = http.User.FindFirstValue("sub")!;
        var evt = new AuditEvent("audit.read", null, null, actorId, null, null, DateTimeOffset.UtcNow);
        try
        {
            await auditPublisher.PublishAsync(evt, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Dotwire.Api.Audit")
                .LogError("Audit publish failed for audit.read by {ActorId}: {Error}", actorId, ex.Message);
            return false;
        }
    }
}
