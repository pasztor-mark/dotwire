using System.Security.Claims;
using System.Text.RegularExpressions;
using Dotwire.Configuration;

namespace Dotwire.Auth;

public static partial class TokenRules
{
    public static string? Validate(ClaimsPrincipal? principal, AuthOptions options, DateTimeOffset now)
    {
        if (principal is null || principal.FindFirstValue("sub") is not { Length: > 0 } sub)
            return "Token has no sub claim.";
        if (!IdValidation.IsValid(sub))
            return "Token sub claim is not a valid id.";
        if (principal.FindFirstValue("dw:role") is not ("member" or "auditor" or "admin"))
            return "Token has no valid dw:role claim.";
        if (!long.TryParse(principal.FindFirstValue("iat"), out var iat))
            return "Token has no iat claim.";
        if (!long.TryParse(principal.FindFirstValue("exp"), out var exp))
            return "Token has no exp claim.";
        if (iat > now.ToUnixTimeSeconds() + options.ClockSkewSeconds)
            return "Token iat is in the future.";
        if (options.MaxTokenLifetimeMinutes > 0 && exp - iat > options.MaxTokenLifetimeMinutes * 60L)
            return "Token lifetime exceeds Auth:MaxTokenLifetimeMinutes.";
        return null;
    }

    public static bool AcceptsQueryToken(string method, PathString path) =>
        HttpMethods.IsGet(method) &&
        (path.StartsWithSegments("/hub/rooms") || SseRoute().IsMatch(path.Value ?? string.Empty));

    [GeneratedRegex("^/rooms/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/events/?$")]
    private static partial Regex SseRoute();
}
