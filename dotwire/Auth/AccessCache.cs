using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Auth;

public readonly record struct AccessCacheReadToken(long Epoch, long StartedAtTimestamp);

public sealed class AccessCache(IOptions<DotwireOptions> options, TimeProvider time)
{
    private readonly long _ttl = options.Value.AuthCacheSeconds * time.TimestampFrequency;
    private readonly ConcurrentDictionary<(Guid, string), (string Role, long ExpiresAt)> _entries = new();
    private readonly ConcurrentDictionary<(Guid, string), long> _tombstones = new();
    private readonly object _gate = new();
    private long _epoch;
    private volatile bool _usable;

    public bool IsUsable => _usable;

    public bool TryGet(Guid roomId, string userId, [NotNullWhen(true)] out string? role)
    {
        role = null;
        if (!_usable) return false;
        var key = (roomId, userId);
        if (!_entries.TryGetValue(key, out var entry)) return false;
        if (time.GetTimestamp() > entry.ExpiresAt)
        {
            _entries.TryRemove(new KeyValuePair<(Guid, string), (string Role, long ExpiresAt)>(key, entry));
            return false;
        }
        if (IsTombstoned(key)) return false;
        role = entry.Role;
        return true;
    }

    public AccessCacheReadToken BeginRead() =>
        new(Interlocked.Read(ref _epoch), time.GetTimestamp());

    public void Store(Guid roomId, string userId, string role, AccessCacheReadToken token)
    {
        var key = (roomId, userId);
        lock (_gate)
        {
            if (!_usable || token.Epoch != _epoch || IsTombstoned(key)) return;
            _entries[key] = (role, token.StartedAtTimestamp + _ttl);
        }
    }

    public void Evict(Guid roomId, string userId)
    {
        var key = (roomId, userId);
        lock (_gate)
        {
            Interlocked.Increment(ref _epoch);
            _tombstones[key] = time.GetTimestamp() + _ttl;
            _entries.TryRemove(key, out _);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _epoch);
            _entries.Clear();
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            _usable = false;
            Interlocked.Increment(ref _epoch);
            _entries.Clear();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _epoch);
            _usable = true;
        }
    }

    public void RemoveExpired()
    {
        var now = time.GetTimestamp();
        foreach (var kv in _entries)
            if (now > kv.Value.ExpiresAt) _entries.TryRemove(kv);
        foreach (var kv in _tombstones)
            if (now > kv.Value) _tombstones.TryRemove(kv);
    }

    private bool IsTombstoned((Guid, string) key) =>
        _tombstones.TryGetValue(key, out var until) && time.GetTimestamp() <= until;
}
