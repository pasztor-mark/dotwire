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
    private static void MapErasureEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/users/{userId}/erase", async (
            string userId,
            EraseUserRequest? request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            AuditPublisher auditPublisher,
            INatsConnection nats,
            IOptions<ComplianceOptions> compliance,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var mode = compliance.Value.Erasure.Mode;
            if (request?.Mode is not null && !ErasureModes.TryParse(request.Mode, out mode))
                return ApiResults.InvalidRequest("mode must be anonymize or delete");

            var actorId = http.User.FindFirstValue("sub")!;
            if (userId == actorId)
                return ApiResults.Conflict("self_erasure", "an admin cannot erase itself");

            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;
            var wireMode = ErasureModes.ToWire(mode);

            long erasureId;
            List<Guid> roomIds;
            bool created;
            await using (var conn = await writeDataSource.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                await LockRoleMutationsAsync(conn, tx, ct);

                if (await ErasureStatusQuery.OpenErasureIdAsync(conn, tx, userId, ct) is { } openId)
                {
                    string openMode;
                    await using (var find = new NpgsqlCommand("SELECT mode FROM erasures WHERE id = $1", conn, tx))
                    {
                        find.Parameters.AddWithValue(openId);
                        openMode = (string)(await find.ExecuteScalarAsync(ct))!;
                    }

                    if (openMode != wireMode)
                        return ApiResults.Conflict("erasure_mode_conflict", $"erasure {openId} is open with mode {openMode}");

                    erasureId = openId;
                    roomIds = [];
                    created = false;
                }
                else
                {
                    var previous = await CurrentRoleForUpdateAsync(conn, tx, userId, ct);
                    if (previous == "admin" && !await HasOtherAdminAsync(conn, tx, userId, ct))
                        return ApiResults.Conflict("last_admin", "cannot erase the last admin");

                    var requested = new AuditEvent("user.erasure.requested", null, null, actorId, userId, (long)(int)mode, DateTimeOffset.UtcNow);
                    if (!await TryAuditAsync(auditPublisher, requested, logger, ct))
                        return ApiResults.AuditUnavailable();

                    await using (var insert = new NpgsqlCommand(
                        "INSERT INTO erasures (user_id, mode, requested_by) VALUES ($1, $2, $3) RETURNING id", conn, tx))
                    {
                        insert.Parameters.AddWithValue(userId);
                        insert.Parameters.AddWithValue(wireMode);
                        insert.Parameters.AddWithValue(actorId);
                        erasureId = (long)(await insert.ExecuteScalarAsync(ct))!;
                    }

                    var revoked = await RevokeUserAccessAsync(conn, tx, auditPublisher, logger, actorId, userId, previous is not null, ct);
                    if (revoked is null)
                        return ApiResults.AuditUnavailable();

                    roomIds = revoked;
                    created = true;
                }

                await tx.CommitAsync(CancellationToken.None);
            }

            if (created)
            {
                foreach (var roomId in roomIds)
                    await PublishRevocationAsync(nats, logger, roomId, userId);

                await RecordRevocationLsnAsync(writeDataSource, logger, erasureId);
                await PublishErasureWakeAsync(nats, logger);
            }

            var status = await ErasureStatusQuery.GetAsync(readDataSource, erasureId, compliance.Value, ct)
                ?? new ErasureStatus(erasureId, userId, wireMode, DateTimeOffset.UtcNow, "in_progress", 0, [], null);
            return Results.Json(status, DotwireJsonContext.Default.ErasureStatus, statusCode: StatusCodes.Status202Accepted);
        });

        group.MapGet("/erasures/{id:long}", async (
            long id,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            IOptions<ComplianceOptions> compliance,
            HttpContext http) =>
        {
            var status = await ErasureStatusQuery.GetAsync(readDataSource, id, compliance.Value, http.RequestAborted);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        group.MapGet("/users/{userId}/erasures", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            IOptions<ComplianceOptions> compliance,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            return Results.Ok(await ErasureStatusQuery.ListForUserAsync(readDataSource, userId, compliance.Value, http.RequestAborted));
        });
    }

    private static async Task RecordRevocationLsnAsync(NpgsqlDataSource writeDataSource, ILogger logger, long erasureId)
    {
        try
        {
            await using var conn = await writeDataSource.OpenConnectionAsync();
            string lsn;
            await using (var read = new NpgsqlCommand("SELECT pg_current_wal_lsn()::text", conn))
                lsn = (string)(await read.ExecuteScalarAsync())!;

            await using var insert = new NpgsqlCommand(
                "INSERT INTO erasure_revocations (erasure_id, lsn) VALUES ($1, $2::pg_lsn) ON CONFLICT DO NOTHING", conn);
            insert.Parameters.AddWithValue(erasureId);
            insert.Parameters.AddWithValue(lsn);
            await insert.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Recording the revocation LSN for erasure {ErasureId} failed; the worker will backfill it: {Error}", erasureId, ex.Message);
        }
    }
}
