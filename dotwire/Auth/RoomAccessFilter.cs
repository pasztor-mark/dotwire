using System.Diagnostics;
using System.Security.Claims;
using Dotwire.Configuration;

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

        var metrics = http.RequestServices.GetRequiredService<DotwireMetrics>();
        var cache = http.RequestServices.GetService<AccessCache>();
        if (cache?.TryGet(roomId, sub, out var cachedRole) == true)
        {
            metrics.AuthCacheLookup(hit: true);
            return cachedRole == claimedRole
                ? await next(context)
                : TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }
        if (cache is not null)
            metrics.AuthCacheLookup(hit: false);

        var token = cache?.BeginRead();
        var batcher = http.RequestServices.GetService<AccessLookupBatcher>();
        var started = Stopwatch.GetTimestamp();
        var (role, isMember) = batcher is not null
            ? await batcher.LookupAsync(roomId, sub, http.RequestAborted)
            : await store.GetRoleAndMembershipAsync(roomId, sub, http.RequestAborted);
        metrics.AuthLookup(batcher is not null ? "batched" : "direct", Stopwatch.GetElapsedTime(started));

        if (role is not null && isMember)
            cache?.Store(roomId, sub, role, token!.Value);

        if (role is null || role != claimedRole || !isMember)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
