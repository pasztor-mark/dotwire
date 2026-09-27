using Dotwire.Auth;
using Dotwire.Data;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Dotwire.Api;

public static class Participants
{
    public static void MapParticipantEndpoints(WebApplication app)
    {
        app.MapGet("/rooms/{roomId}/participants", async (
                string roomId,
                [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
                HttpContext http) =>
            {
                var roomGuid = Guid.Parse(roomId); // RoomMembershipFilter validated the route value.
                var userIds = new List<string>();
                await using var cmd = readDataSource.CreateCommand(
                    "SELECT user_id FROM room_members WHERE room_id = $1 ORDER BY user_id ASC");
                cmd.Parameters.AddWithValue(roomGuid);
                await using var reader = await cmd.ExecuteReaderAsync(http.RequestAborted);
                while (await reader.ReadAsync(http.RequestAborted))
                    userIds.Add(reader.GetString(0));

                return Results.Json(userIds.ToArray(), DotwireJsonContext.Default.StringArray);
            })
            .RequireAuthorization()
            .AddEndpointFilter<RoleCrossCheckFilter>()
            .AddEndpointFilter<RoomMembershipFilter>()
            .RequireRateLimiting("read");
    }
}
