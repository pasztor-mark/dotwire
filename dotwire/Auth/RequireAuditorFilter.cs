using System.Security.Claims;
using Dotwire.Data;
using Npgsql;

namespace Dotwire.Auth;

/// <summary>
/// Mirrors <see cref="RequireAdminFilter"/> for the `/audit*` routes (spec §3.7): the
/// `auditor` role only. An admin token is rejected here - admin does not get audit access
/// (AUTH.md, COMPLIANCE.md).
/// </summary>
public sealed class RequireAuditorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        var sub = user.FindFirstValue("sub");
        var claimedRole = user.FindFirstValue("dw:role");

        if (string.IsNullOrEmpty(sub) || claimedRole != "auditor")
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        var dataSource = context.HttpContext.RequestServices
            .GetRequiredKeyedService<NpgsqlDataSource>(PostgresDataSources.Read);

        await using var cmd = dataSource.CreateCommand(
            "SELECT role FROM user_roles WHERE user_id = $1");
        cmd.Parameters.AddWithValue(sub);
        var tableRole = (string?)await cmd.ExecuteScalarAsync(context.HttpContext.RequestAborted);

        if (tableRole != "auditor")
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
