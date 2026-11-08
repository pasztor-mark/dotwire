using System.Security.Claims;
using System.Threading.RateLimiting;
using Dotwire.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Dotwire.Realtime;

public sealed class HubRateLimitFilter(IOptions<RateLimitOptions> options) : IHubFilter, IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ =>
        {
            var bucket = options.Value.Hub;
            return new TokenBucketRateLimiterOptions
            {
                TokenLimit = bucket.PermitLimit,
                TokensPerPeriod = bucket.TokensPerPeriod,
                ReplenishmentPeriod = TimeSpan.FromSeconds(bucket.PeriodSeconds),
                AutoReplenishment = true,
                QueueLimit = 0,
            };
        }));

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (options.Value.Enabled && invocationContext.HubMethodName is "Subscribe" or "Unsubscribe")
        {
            using var lease = _limiter.AttemptAcquire(PartitionKey(invocationContext.Context));
            if (!lease.IsAcquired)
                throw new HubException("RateLimited");
        }

        return await next(invocationContext);
    }

    public static string PartitionKey(HubCallerContext context) =>
        context.User?.FindFirstValue("sub") is { Length: > 0 } sub ? $"sub:{sub}" : $"conn:{context.ConnectionId}";

    public void Dispose() => _limiter.Dispose();
}
