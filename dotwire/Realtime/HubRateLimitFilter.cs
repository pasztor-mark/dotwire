using System.Threading.RateLimiting;
using Dotwire.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Dotwire.Realtime;

/// <summary>
/// Per-connection token bucket for hub method invocations (spec §3.10). Applies only to
/// <c>Subscribe</c>/<c>Unsubscribe</c> - <c>Typing</c> keeps its own half-second cooldown and
/// is dropped silently, not rate limited here.
/// </summary>
public sealed class HubRateLimitFilter(IOptions<RateLimitOptions> options) : IHubFilter, IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(connectionId =>
        RateLimitPartition.GetTokenBucketLimiter(connectionId, _ =>
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
            using var lease = _limiter.AttemptAcquire(invocationContext.Context.ConnectionId);
            if (!lease.IsAcquired)
                throw new HubException("RateLimited");
        }

        return await next(invocationContext);
    }

    public void Dispose() => _limiter.Dispose();
}
