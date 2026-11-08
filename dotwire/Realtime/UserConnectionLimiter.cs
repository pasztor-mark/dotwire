using System.Collections.Concurrent;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Realtime;

public sealed class UserConnectionLimiter(IOptions<DotwireOptions> options)
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public int CountFor(string userId) => _counts.TryGetValue(userId, out var count) ? count : 0;

    public bool TryAcquire(string userId)
    {
        var max = options.Value.MaxConnectionsPerUser;
        while (true)
        {
            var current = _counts.GetOrAdd(userId, 0);
            if (max > 0 && current >= max)
            {
                if (current == 0)
                    _counts.TryRemove(new KeyValuePair<string, int>(userId, 0));
                return false;
            }
            if (_counts.TryUpdate(userId, current + 1, current))
                return true;
        }
    }

    public void Release(string userId)
    {
        while (true)
        {
            if (!_counts.TryGetValue(userId, out var current))
                return;
            if (current <= 1)
            {
                if (_counts.TryRemove(new KeyValuePair<string, int>(userId, current)))
                    return;
            }
            else if (_counts.TryUpdate(userId, current - 1, current))
            {
                return;
            }
        }
    }
}
