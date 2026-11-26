using System.Security.Claims;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Npgsql;

namespace Dotwire.Api;

public static partial class Admin
{
    private const string ErasureWakeSubject = ErasureWorkerService.WakeSubject;
    private static readonly TimeSpan HoldRetentionPollInterval = TimeSpan.FromMilliseconds(500);

    private static void MapHoldEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/holds", async (
            PlaceHoldRequest request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            IOptions<ComplianceOptions> compliance,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            var hasRoom = request.RoomId is not null;
            var hasUser = request.UserId is not null;
            if (hasRoom == hasUser)
                return ApiResults.InvalidRequest("exactly one of roomId or userId is required");
            if (hasUser && !IdValidation.IsValid(request.UserId))
                return ApiResults.InvalidUserId();
            if (!IdValidation.IsValid(request.MatterRef))
                return ApiResults.InvalidRequest("matterRef must be 1-256 characters with no control characters");

            var matterRef = request.MatterRef!;
            var actorId = http.User.FindFirstValue("sub")!;
            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;

            await using var conn = await writeDataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await LockHoldsExclusiveAsync(conn, tx, ct);

            long id;
            DateTimeOffset placedAt;
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO legal_holds (scope, room_id, user_id, matter_ref, placed_by) VALUES ($1, $2, $3, $4, $5) RETURNING id, placed_at", conn, tx))
            {
                insert.Parameters.AddWithValue(hasRoom ? "room" : "user");
                insert.Parameters.AddWithValue((object?)request.RoomId ?? DBNull.Value);
                insert.Parameters.AddWithValue((object?)request.UserId ?? DBNull.Value);
                insert.Parameters.AddWithValue(matterRef);
                insert.Parameters.AddWithValue(actorId);
                await using var reader = await insert.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                id = reader.GetInt64(0);
                placedAt = reader.GetFieldValue<DateTimeOffset>(1);
            }

            await SyncRetentionScheduleAsync(conn, tx, ct);

            var audit = new AuditEvent("hold.placed", request.RoomId, null, actorId, request.UserId, id, DateTimeOffset.UtcNow);
            if (!await TryAuditAsync(auditPublisher, audit, logger, ct))
                return ApiResults.AuditUnavailable();

            await tx.CommitAsync(CancellationToken.None);

            await WaitForRetentionIdleAsync(writeDataSource, compliance.Value.HoldRetentionWaitSeconds, logger, ct);

            return Results.Created($"/admin/holds/{id}",
                new LegalHold(id, hasRoom ? "room" : "user", request.RoomId, request.UserId, matterRef, actorId, placedAt, null, null));
        });

        group.MapDelete("/holds/{id:long}", async (
            long id,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            INatsConnection nats,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            var actorId = http.User.FindFirstValue("sub")!;
            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;

            await using var conn = await writeDataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await LockHoldsExclusiveAsync(conn, tx, ct);

            Guid? roomId;
            string? userId;
            bool released;
            await using (var find = new NpgsqlCommand(
                "SELECT h.room_id, h.user_id, EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id) FROM legal_holds h WHERE h.id = $1", conn, tx))
            {
                find.Parameters.AddWithValue(id);
                await using var reader = await find.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return Results.NotFound();
                roomId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                userId = reader.IsDBNull(1) ? null : reader.GetString(1);
                released = reader.GetBoolean(2);
            }

            if (released)
                return ApiResults.Conflict("already_released");

            await using (var release = new NpgsqlCommand("INSERT INTO legal_hold_releases (hold_id, released_by) VALUES ($1, $2)", conn, tx))
            {
                release.Parameters.AddWithValue(id);
                release.Parameters.AddWithValue(actorId);
                await release.ExecuteNonQueryAsync(ct);
            }

            await SyncRetentionScheduleAsync(conn, tx, ct);

            var audit = new AuditEvent("hold.released", roomId, null, actorId, userId, id, DateTimeOffset.UtcNow);
            if (!await TryAuditAsync(auditPublisher, audit, logger, ct))
                return ApiResults.AuditUnavailable();

            await tx.CommitAsync(CancellationToken.None);

            await PublishErasureWakeAsync(nats, logger);

            return Results.NoContent();
        });

        group.MapGet("/holds", async (
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http,
            bool active = true) =>
        {
            var ct = http.RequestAborted;
            var holds = new List<LegalHold>();
            await using var cmd = readDataSource.CreateCommand(
                "SELECT h.id, h.scope, h.room_id, h.user_id, h.matter_ref, h.placed_by, h.placed_at, r.released_at, r.released_by " +
                "FROM legal_holds h LEFT JOIN legal_hold_releases r ON r.hold_id = h.id " +
                "WHERE (r.hold_id IS NULL) = $1 ORDER BY h.id");
            cmd.Parameters.AddWithValue(active);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                holds.Add(ReadHold(reader));
            return Results.Ok(holds.ToArray());
        });
    }

    private static LegalHold ReadHold(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static async Task LockHoldsExclusiveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('dotwire.legal_hold', 0))", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SyncRetentionScheduleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT sync_retention_schedule()", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WaitForRetentionIdleAsync(
        NpgsqlDataSource writeDataSource, int waitSeconds, ILogger logger, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        await using var conn = await writeDataSource.OpenConnectionAsync(ct);
        while (true)
        {
            await using var cmd = new NpgsqlCommand("SELECT retention_job_running()", conn);
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!)
                return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogWarning(
                    "Retention job still running {WaitSeconds}s after a legal hold was placed; chunks it already dropped cannot be restored",
                    waitSeconds);
                return;
            }

            await Task.Delay(HoldRetentionPollInterval, ct);
        }
    }

    private static async Task PublishErasureWakeAsync(INatsConnection nats, ILogger logger)
    {
        using var cts = new CancellationTokenSource(RevocationAttemptTimeout);
        try
        {
            await nats.PublishAsync<byte[]>(ErasureWakeSubject, Array.Empty<byte>(), cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Erasure wake publish failed: {Error}", ex.Message);
        }
    }
}
