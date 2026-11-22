using System.Threading.RateLimiting;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Api;

public sealed class SendRateLimiter(IOptions<RateLimitOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ =>
        {
            var bucket = options.Value.Send;
            return new TokenBucketRateLimiterOptions
            {
                TokenLimit = bucket.PermitLimit,
                TokensPerPeriod = bucket.TokensPerPeriod,
                ReplenishmentPeriod = TimeSpan.FromSeconds(bucket.PeriodSeconds),
                AutoReplenishment = true,
                QueueLimit = 0,
            };
        }));

    public bool TryAcquire(string sub, out int retryAfterSeconds)
    {
        retryAfterSeconds = 1;
        if (!options.Value.Enabled)
            return true;

        using var lease = _limiter.AttemptAcquire($"user:{sub}");
        if (lease.IsAcquired)
            return true;

        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        return false;
    }

    public void Dispose() => _limiter.Dispose();
}

public sealed class SendRateLimitFilter(SendRateLimiter limiter, DotwireMetrics metrics) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsPost(http.Request.Method)
            && http.User.FindFirst("sub")?.Value is { } sub
            && !limiter.TryAcquire(sub, out var retryAfterSeconds))
        {
            metrics.Rejection("rest", "rate_limited", "send");
            return ApiResults.RateLimited(retryAfterSeconds);
        }

        return await next(context);
    }
}
