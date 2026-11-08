using System.Security.Claims;

namespace Dotwire.Auth;

public sealed class RoomMembershipFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["roomId"] as string, out var roomId))
            return TypedResults.NotFound();

        var sub = http.User.FindFirstValue("sub")!;
        var store = http.RequestServices.GetRequiredService<IAccessStore>();

        if (!await store.IsMemberAsync(roomId, sub, http.RequestAborted))
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
