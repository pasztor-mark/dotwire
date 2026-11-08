using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dotwire.Realtime;

public sealed class TypingThrottle
{
    public static readonly long CooldownTicks = Stopwatch.Frequency / 2;
    private static readonly long RetentionTicks = Stopwatch.Frequency * 60;

    private sealed class State
    {
        public bool Seen;
        public long TrueTicks;
        public bool IsTyping;
        public long TouchedTicks;
    }

    private readonly ConcurrentDictionary<(string UserId, Guid RoomId), State> _states = new();
    private long _lastPruneTicks;

    public int Count => _states.Count;

    public bool ShouldPublish(string userId, Guid roomId, bool isTyping, long now)
    {
        Prune(now);

        var state = _states.GetOrAdd((userId, roomId), _ => new State());
        lock (state)
        {
            state.TouchedTicks = now;
            var blocked = isTyping
                ? state.Seen && now - state.TrueTicks < CooldownTicks
                : !(state.Seen && state.IsTyping);
            if (blocked)
                return false;

            if (isTyping)
                state.TrueTicks = now;
            state.IsTyping = isTyping;
            state.Seen = true;
            return true;
        }
    }

    private void Prune(long now)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now - last < RetentionTicks / 2 || Interlocked.CompareExchange(ref _lastPruneTicks, now, last) != last)
            return;

        foreach (var (key, state) in _states)
        {
            if (now - Volatile.Read(ref state.TouchedTicks) > RetentionTicks)
                _states.TryRemove(new KeyValuePair<(string, Guid), State>(key, state));
        }
    }
}
