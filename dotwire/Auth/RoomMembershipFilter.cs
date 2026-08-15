using System.Security.Claims;
using Dotwire.Data;
using Npgsql;

namespace Dotwire.Auth;

/// <summary>
/// AUTH.md pipeline stage 4 for REST: room_members decides, per request. (The
/// check-at-CONNECT caching contract is SignalR's - REST is not the fanout hot path.)
/// Also owns roomId parsing: a non-uuid room can't exist, so it's a 404, not a 400.
/// </summary>
public sealed class RoomMembershipFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["roomId"] as string, out var roomId))
            return TypedResults.NotFound();

        var sub = http.User.FindFirstValue("sub")!;
        var dataSource = http.RequestServices
            .GetRequiredKeyedService<NpgsqlDataSource>(PostgresDataSources.Read);

        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM room_members WHERE room_id = $1 AND user_id = $2)");
        cmd.Parameters.AddWithValue(roomId);
        cmd.Parameters.AddWithValue(sub);
        var isMember = (bool)(await cmd.ExecuteScalarAsync(http.RequestAborted))!;

        if (!isMember)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
