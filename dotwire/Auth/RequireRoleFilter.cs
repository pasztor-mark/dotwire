using System.Security.Claims;

namespace Dotwire.Auth;

public sealed class RequireRoleFilter(string role) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        var sub = http.User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub) || http.User.FindFirstValue("dw:role") != role)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        var store = http.RequestServices.GetRequiredService<IAccessStore>();
        if (await store.GetRoleAsync(sub, http.RequestAborted) != role)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
