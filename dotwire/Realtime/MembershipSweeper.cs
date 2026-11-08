using Dotwire.Auth;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Realtime;

public sealed class MembershipSweeper(
    RoomInterestManager interestManager,
    IAccessStore accessStore,
    IOptions<DotwireOptions> options,
    ILogger<MembershipSweeper> logger) : BackgroundService
{
    private const int ChunkSize = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.MembershipSweepSeconds;
        if (seconds <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Membership sweep failed: {Error}", ex.Message);
            }
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var live = interestManager.LiveSubscriptions();
        if (live.Count == 0)
            return 0;

        var members = new HashSet<(Guid, string)>();
        foreach (var chunk in live.Select(l => (l.RoomId, l.UserId)).Distinct().Chunk(ChunkSize))
            members.UnionWith(await accessStore.FilterMembersAsync(chunk, ct));

        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in live.Select(l => l.UserId).Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            foreach (var (userId, role) in await accessStore.GetRolesAsync(chunk, ct))
                roles[userId] = role;
        }

        var stale = live
            .Where(l => !members.Contains((l.RoomId, l.UserId))
                        || (l.Role is not null && (!roles.TryGetValue(l.UserId, out var role) || role != l.Role)))
            .Select(l => (l.RoomId, l.UserId))
            .Distinct()
            .ToList();

        foreach (var (roomId, userId) in stale)
            interestManager.Revoke(roomId, userId);

        if (stale.Count > 0)
            logger.LogInformation("Membership sweep revoked {Count} live subscriptions", stale.Count);
        return stale.Count;
    }
}
