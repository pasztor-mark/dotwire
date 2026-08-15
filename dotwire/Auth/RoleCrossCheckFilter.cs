using System.Security.Claims;
using Dotwire.Data;
using Npgsql;

namespace Dotwire.Auth;

/// <summary>
/// AUTH.md pipeline stage 3: the token's dw:role is a hint; user_roles is authoritative.
/// Missing row or mismatch denies - a revocation in the table takes effect immediately,
/// independent of outstanding token TTLs. Read pool: authorization is a read.
/// </summary>
public sealed class RoleCrossCheckFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        var sub = user.FindFirstValue("sub")!;         // OnTokenValidated guarantees both
        var claimedRole = user.FindFirstValue("dw:role")!;

        var dataSource = context.HttpContext.RequestServices
            .GetRequiredKeyedService<NpgsqlDataSource>(PostgresDataSources.Read);

        await using var cmd = dataSource.CreateCommand(
            "SELECT role FROM user_roles WHERE user_id = $1");
        cmd.Parameters.AddWithValue(sub);
        var tableRole = (string?)await cmd.ExecuteScalarAsync(context.HttpContext.RequestAborted);

        if (tableRole is null || tableRole != claimedRole)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
