using System.Security.Claims;
using Dotwire.Auth;
using Dotwire.Data;
using Dotwire.Nats;
using NATS.Client.Core;
using Npgsql;

namespace Dotwire.Api;

public static partial class Admin
{
    private static void MapRoleEndpoints(RouteGroupBuilder group)
    {
        group.MapPut("/users/{userId}/role", async (
            string userId,
            SetUserRoleRequest request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            INatsConnection nats,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            if (request.Role is not ("member" or "auditor" or "admin"))
                return Results.BadRequest();

            var actorId = http.User.FindFirstValue("sub")!;
            if (userId == actorId)
                return ApiResults.Conflict("self_role_change", "an admin cannot change its own role");

            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;

            await using var conn = await writeDataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await LockRoleMutationsAsync(conn, tx, ct);

            if (await ErasureStatusQuery.OpenErasureIdAsync(conn, tx, userId, ct) is not null)
                return ApiResults.Conflict("erasure_open", $"erasure open for {userId}");

            var previous = await CurrentRoleForUpdateAsync(conn, tx, userId, ct);
            if (previous == "admin" && request.Role != "admin" && !await HasOtherAdminAsync(conn, tx, userId, ct))
                return ApiResults.Conflict("last_admin", "cannot demote the last admin");

            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO user_roles (user_id, role) VALUES ($1, $2) ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role", conn, tx))
            {
                cmd.Parameters.AddWithValue(userId);
                cmd.Parameters.AddWithValue(request.Role);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var roomIds = new List<Guid>();
            if (previous is not null && previous != request.Role)
            {
                await using var rooms = new NpgsqlCommand("SELECT room_id FROM room_members WHERE user_id = $1", conn, tx);
                rooms.Parameters.AddWithValue(userId);
                await using var reader = await rooms.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    roomIds.Add(reader.GetGuid(0));
            }

            var audit = new AuditEvent($"role.set.{request.Role}", null, null, actorId, userId, null, DateTimeOffset.UtcNow);
            if (!await TryAuditAsync(auditPublisher, audit, logger, ct))
                return ApiResults.AuditUnavailable();

            await tx.CommitAsync(CancellationToken.None);

            foreach (var roomId in roomIds)
                await PublishRevocationAsync(nats, logger, roomId, userId);

            return Results.Ok(new UserRoleResponse(userId, request.Role));
        });

        group.MapGet("/users/{userId}/role", async (
            string userId,
            IAccessStore accessStore,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var role = await accessStore.GetRoleAsync(userId, http.RequestAborted);
            return role is null ? Results.NotFound() : Results.Ok(new UserRoleResponse(userId, role));
        });

        group.MapDelete("/users/{userId}/role", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            INatsConnection nats,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var actorId = http.User.FindFirstValue("sub")!;
            if (userId == actorId)
                return ApiResults.Conflict("self_role_change", "an admin cannot delete its own role");

            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;

            await using var conn = await writeDataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await LockRoleMutationsAsync(conn, tx, ct);

            var previous = await CurrentRoleForUpdateAsync(conn, tx, userId, ct);
            if (previous == "admin" && !await HasOtherAdminAsync(conn, tx, userId, ct))
                return ApiResults.Conflict("last_admin", "cannot delete the last admin");

            var roomIds = await RevokeUserAccessAsync(conn, tx, auditPublisher, logger, actorId, userId, true, ct);
            if (roomIds is null)
                return ApiResults.AuditUnavailable();

            await tx.CommitAsync(CancellationToken.None);

            foreach (var roomId in roomIds)
                await PublishRevocationAsync(nats, logger, roomId, userId);

            return Results.NoContent();
        });
    }
}
