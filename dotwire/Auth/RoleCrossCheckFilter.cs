using System.Security.Claims;

namespace Dotwire.Auth;

public sealed class RoleCrossCheckFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sub = http.User.FindFirstValue("sub")!;
        var claimedRole = http.User.FindFirstValue("dw:role")!;

        var store = http.RequestServices.GetRequiredService<IAccessStore>();
        var tableRole = await store.GetRoleAsync(sub, http.RequestAborted);

        if (tableRole is null || tableRole != claimedRole)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
