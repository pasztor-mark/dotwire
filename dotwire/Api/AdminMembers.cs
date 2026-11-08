using System.Security.Claims;
using Dotwire.Auth;
using Dotwire.Data;
using Dotwire.Nats;
using NATS.Client.Core;
using Npgsql;

namespace Dotwire.Api;

public static partial class Admin
{
    private static void MapMemberEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/rooms/{roomId}/members", async (
            Guid roomId,
            AddMembersRequest request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (request.UserIds is null || request.UserIds.Length == 0)
                return Results.BadRequest();

            if (request.UserIds.Length > MaxMembersPerRequest)
                return ApiResults.InvalidRequest($"at most {MaxMembersPerRequest} userIds per request");

            foreach (var userId in request.UserIds)
            {
                if (!IdValidation.IsValid(userId))
                    return ApiResults.InvalidUserId();
            }

            var ensureMemberRole = request.EnsureMemberRole ?? false;
            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;
            var actorId = http.User.FindFirstValue("sub")!;

            await using (var conn = await writeDataSource.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                var grantedMemberRole = new List<string>();
                foreach (var userId in request.UserIds)
                {
                    await using (var cmd = new NpgsqlCommand(
                        "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn, tx))
                    {
                        cmd.Parameters.AddWithValue(roomId);
                        cmd.Parameters.AddWithValue(userId);
                        await cmd.ExecuteNonQueryAsync(ct);
                    }

                    if (ensureMemberRole)
                    {
                        await using var roleCmd = new NpgsqlCommand(
                            "INSERT INTO user_roles (user_id, role) VALUES ($1, 'member') ON CONFLICT DO NOTHING RETURNING user_id", conn, tx);
                        roleCmd.Parameters.AddWithValue(userId);
                        if (await roleCmd.ExecuteScalarAsync(ct) is string inserted)
                            grantedMemberRole.Add(inserted);
                    }
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var userId in grantedMemberRole)
                {
                    if (!await TryAuditAsync(auditPublisher, new AuditEvent("role.set.member", null, null, actorId, userId, null, now), logger, ct))
                        return ApiResults.AuditUnavailable();
                }

                foreach (var userId in request.UserIds)
                {
                    if (!await TryAuditAsync(auditPublisher, new AuditEvent("member.added", roomId, null, actorId, userId, null, now), logger, ct))
                        return ApiResults.AuditUnavailable();
                }

                await tx.CommitAsync(CancellationToken.None);
            }

            return Results.Ok(new RoomMembersResponse(roomId, request.UserIds));
        });

        group.MapDelete("/rooms/{roomId}/members/{userId}", async (
            Guid roomId,
            string userId,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            INatsConnection nats,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var logger = AdminLogger(loggerFactory);
            var ct = http.RequestAborted;

            await using var conn = await writeDataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(
                "DELETE FROM room_members WHERE room_id = $1 AND user_id = $2", conn, tx))
            {
                cmd.Parameters.AddWithValue(roomId);
                cmd.Parameters.AddWithValue(userId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var actorId = http.User.FindFirstValue("sub")!;
            var audit = new AuditEvent("member.removed", roomId, null, actorId, userId, null, DateTimeOffset.UtcNow);
            if (!await TryAuditAsync(auditPublisher, audit, logger, ct))
                return ApiResults.AuditUnavailable();

            await tx.CommitAsync(CancellationToken.None);

            await PublishRevocationAsync(nats, logger, roomId, userId);

            return Results.NoContent();
        });

        group.MapGet("/rooms/{roomId}/members", async (
            Guid roomId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http) =>
        {
            var members = new List<string>();
            await using var cmd = readDataSource.CreateCommand(
                "SELECT user_id FROM room_members WHERE room_id = $1 ORDER BY user_id ASC");
            cmd.Parameters.AddWithValue(roomId);
            await using var reader = await cmd.ExecuteReaderAsync(http.RequestAborted);
            while (await reader.ReadAsync(http.RequestAborted))
                members.Add(reader.GetString(0));

            return Results.Ok(new RoomMembersResponse(roomId, members.ToArray()));
        });

        group.MapGet("/users/{userId}/rooms", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var roomIds = new List<Guid>();
            await using var cmd = readDataSource.CreateCommand(
                "SELECT room_id FROM room_members WHERE user_id = $1");
            cmd.Parameters.AddWithValue(userId);
            await using var reader = await cmd.ExecuteReaderAsync(http.RequestAborted);
            while (await reader.ReadAsync(http.RequestAborted))
                roomIds.Add(reader.GetGuid(0));

            return Results.Ok(new UserRoomsResponse(userId, roomIds.ToArray()));
        });
    }
}
