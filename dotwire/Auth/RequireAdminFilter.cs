using System.Security.Claims;
using Dotwire.Data;
using Npgsql;

namespace Dotwire.Auth;

public sealed class RequireAdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        var sub = user.FindFirstValue("sub");
        var claimedRole = user.FindFirstValue("dw:role");

        if (string.IsNullOrEmpty(sub) || claimedRole != "admin")
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        var dataSource = context.HttpContext.RequestServices
            .GetRequiredKeyedService<NpgsqlDataSource>(PostgresDataSources.Read);

        await using var cmd = dataSource.CreateCommand(
            "SELECT role FROM user_roles WHERE user_id = $1");
        cmd.Parameters.AddWithValue(sub);
        var tableRole = (string?)await cmd.ExecuteScalarAsync(context.HttpContext.RequestAborted);

        if (tableRole != "admin")
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
