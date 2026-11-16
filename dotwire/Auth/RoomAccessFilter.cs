using System.Security.Claims;

namespace Dotwire.Auth;

public sealed class RoomAccessFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sub = http.User.FindFirstValue("sub")!;
        var claimedRole = http.User.FindFirstValue("dw:role")!;
        var store = http.RequestServices.GetRequiredService<IAccessStore>();

        if (!Guid.TryParse(http.Request.RouteValues["roomId"] as string, out var roomId))
            return await store.GetRoleAsync(sub, http.RequestAborted) == claimedRole
                ? TypedResults.NotFound()
                : TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        var (role, isMember) = await store.GetRoleAndMembershipAsync(roomId, sub, http.RequestAborted);
        if (role is null || role != claimedRole || !isMember)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
