namespace Dotwire.Api;

/// <summary>
/// Typed failure bodies for the new error contract (spec §3.14): every non-bare 4xx/5xx
/// response carries `{ "error": "&lt;code&gt;", "reason"?: "&lt;text&gt;" }`. Existing bare
/// 401/403/404 responses stay bare - only the codes below get a body.
/// </summary>
public static class ApiResults
{
    public static IResult InvalidUserId() =>
        Error(StatusCodes.Status400BadRequest, "invalid_user_id");

    public static IResult InvalidContent() =>
        Error(StatusCodes.Status400BadRequest, "invalid_content");

    public static IResult InvalidRequest(string? reason = null) =>
        Error(StatusCodes.Status400BadRequest, "invalid_request", reason);

    public static IResult PresendRejected(string? reason = null) =>
        Error(StatusCodes.Status422UnprocessableEntity, "presend_rejected", reason);

    public static IResult RateLimited(int retryAfterSeconds)
    {
        var result = Error(StatusCodes.Status429TooManyRequests, "rate_limited");
        return new RetryAfterResult(result, retryAfterSeconds);
    }

    public static IResult PresendUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, "presend_unavailable");

    public static IResult AuditUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, "audit_unavailable");

    public static IResult StreamUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, "stream_unavailable");

    private static IResult Error(int statusCode, string code, string? reason = null) =>
        Results.Json(new ApiErrorResponse(code, reason), DotwireJsonContext.Default.ApiErrorResponse, statusCode: statusCode);

    /// <summary>Wraps an <see cref="IResult"/> to also set the `Retry-After` header (§3.14: "ceiling of the wait for one token").</summary>
    private sealed class RetryAfterResult(IResult inner, int retryAfterSeconds) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            await inner.ExecuteAsync(httpContext);
        }
    }
}
