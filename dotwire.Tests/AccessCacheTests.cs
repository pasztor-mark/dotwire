using Dotwire.Auth;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class AccessCacheTests
{
    private static readonly Guid Room = Guid.NewGuid();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public DateTimeOffset UtcNow
        {
            get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static (AccessCache Cache, ManualTimeProvider Time) New(bool resume = true)
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new AccessCache(Options.Create(new DotwireOptions { AuthCacheSeconds = 30 }), time);
        if (resume) cache.Resume();
        return (cache, time);
    }

    [Fact]
    public void NotUsableUntilResume()
    {
        var (cache, _) = New(resume: false);
        Assert.False(cache.IsUsable);
        cache.Store(Room, "u", "member", cache.BeginRead());
        Assert.False(cache.TryGet(Room, "u", out _));
        cache.Resume();
        Assert.True(cache.IsUsable);
        cache.Store(Room, "u", "member", cache.BeginRead());
        Assert.True(cache.TryGet(Room, "u", out var role));
        Assert.Equal("member", role);
    }

    [Fact]
    public void Expiry_IsAbsoluteFromReadStart()
    {
        var (cache, time) = New();
        var t0 = time.UtcNow;
        var token = cache.BeginRead();
        time.Advance(TimeSpan.FromSeconds(10));
        cache.Store(Room, "u", "member", token);
        Assert.True(cache.TryGet(Room, "u", out _));
        time.UtcNow = t0 + Ttl + TimeSpan.FromTicks(1);
        Assert.False(cache.TryGet(Room, "u", out _));
    }

    [Fact]
    public void Store_AfterEvictDuringRead_IsDropped()
    {
        var (cache, _) = New();
        var token = cache.BeginRead();
        cache.Evict(Room, "u");
        cache.Store(Room, "u", "member", token);
        Assert.False(cache.TryGet(Room, "u", out _));
    }

    [Fact]
    public void Store_WhileTombstoned_IsDropped()
    {
        var (cache, time) = New();
        cache.Evict(Room, "u");
        cache.Store(Room, "u", "member", cache.BeginRead());
        Assert.False(cache.TryGet(Room, "u", out _));
        time.Advance(Ttl + TimeSpan.FromTicks(1));
        cache.Store(Room, "u", "member", cache.BeginRead());
        Assert.True(cache.TryGet(Room, "u", out _));
    }

    [Fact]
    public void Evict_OtherKeyUnaffectedButEpochBlocksInflight()
    {
        var (cache, _) = New();
        cache.Store(Room, "k2", "member", cache.BeginRead());
        var stale = cache.BeginRead();
        cache.Evict(Room, "k1");
        Assert.True(cache.TryGet(Room, "k2", out _));
        cache.Store(Room, "k3", "member", stale);
        Assert.False(cache.TryGet(Room, "k3", out _));
    }

    [Fact]
    public void Flush_ClearsEntriesKeepsTombstonesStaysUsable()
    {
        var (cache, _) = New();
        cache.Store(Room, "a", "member", cache.BeginRead());
        cache.Evict(Room, "b");
        cache.Flush();
        Assert.True(cache.IsUsable);
        Assert.False(cache.TryGet(Room, "a", out _));
        cache.Store(Room, "b", "member", cache.BeginRead());
        Assert.False(cache.TryGet(Room, "b", out _));
        cache.Store(Room, "a", "member", cache.BeginRead());
        Assert.True(cache.TryGet(Room, "a", out _));
    }

    [Fact]
    public void Suspend_ClearsAndBlocks_ResumeReenables()
    {
        var (cache, _) = New();
        cache.Store(Room, "a", "member", cache.BeginRead());
        cache.Evict(Room, "b");
        cache.Suspend();
        Assert.False(cache.IsUsable);
        Assert.False(cache.TryGet(Room, "a", out _));
        cache.Store(Room, "c", "member", cache.BeginRead());
        var beforeResume = cache.BeginRead();
        cache.Resume();
        Assert.False(cache.TryGet(Room, "c", out _));
        cache.Store(Room, "c", "member", beforeResume);
        Assert.False(cache.TryGet(Room, "c", out _));
        cache.Store(Room, "b", "member", cache.BeginRead());
        Assert.False(cache.TryGet(Room, "b", out _));
        cache.Store(Room, "c", "member", cache.BeginRead());
        Assert.True(cache.TryGet(Room, "c", out _));
    }

    [Fact]
    public void RemoveExpired_DropsExpiredEntriesAndTombstones()
    {
        var (cache, time) = New();
        cache.Store(Room, "a", "member", cache.BeginRead());
        cache.Evict(Room, "b");
        time.Advance(Ttl + TimeSpan.FromTicks(1));
        cache.RemoveExpired();
        Assert.False(cache.TryGet(Room, "a", out _));
        cache.Store(Room, "b", "member", cache.BeginRead());
        Assert.True(cache.TryGet(Room, "b", out _));
    }

    [Fact]
    public void Race_StoreVsEvict_NeverServesAfterEvictReturns()
    {
        const int rounds = 50_000;
        var (cache, time) = New();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var barrier = new Barrier(2);
        var evicted = 0;
        long stores = 0;
        var timeout = TimeSpan.FromSeconds(30);
        var ct = TestContext.Current.CancellationToken;

        var reader = new Thread(() =>
        {
            try
            {
                for (var r = 0; r < rounds; r++)
                {
                    if (!barrier.SignalAndWait(timeout, ct)) { failures.Enqueue("reader start timeout"); return; }
                    var last = cache.BeginRead();
                    while (Volatile.Read(ref evicted) == 0)
                    {
                        last = cache.BeginRead();
                        cache.Store(Room, "u", "member", last);
                        stores++;
                    }
                    if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: reader hit after evict");
                    cache.Store(Room, "u", "member", last);
                    if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: reader hit after late store");
                    cache.Store(Room, "u", "member", cache.BeginRead());
                    if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: reader hit after tombstoned store");
                    if (!barrier.SignalAndWait(timeout, ct)) { failures.Enqueue("reader end timeout"); return; }
                }
            }
            catch (OperationCanceledException) { failures.Enqueue("reader cancelled"); }
        }) { IsBackground = true };
        reader.Start();

        var rnd = new Random(12345);
        for (var r = 0; r < rounds; r++)
        {
            var preEvict = cache.BeginRead();
            if (!barrier.SignalAndWait(timeout, ct)) { failures.Enqueue("evictor start timeout"); break; }
            Thread.SpinWait(rnd.Next(0, 400));
            cache.Evict(Room, "u");
            if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: evictor hit right after evict");
            Volatile.Write(ref evicted, 1);
            for (var i = 0; i < 8; i++)
                if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: evictor hit after evict ({i})");
            if (!barrier.SignalAndWait(timeout, ct)) { failures.Enqueue("evictor end timeout"); break; }

            if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: hit at end of live tombstone");
            time.Advance(Ttl + TimeSpan.FromTicks(1));
            if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: hit after tombstone expiry before new read");
            cache.Store(Room, "u", "member", preEvict);
            if (cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: pre-evict token served after expiry");
            cache.Store(Room, "u", "member", cache.BeginRead());
            if (!cache.TryGet(Room, "u", out _)) failures.Enqueue($"round {r}: fresh read after expiry not cached");
            Volatile.Write(ref evicted, 0);
        }

        Assert.True(reader.Join(timeout));
        Assert.Empty(failures);
        Assert.True(stores > rounds / 2, $"only {stores} stores");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Race_StoreVsGlobalInvalidation_NeverServesPreInvalidationReads(bool suspendResume)
    {
        const int rounds = 20_000;
        var (cache, _) = New();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stop = 0;
        long stores = 0;

        var reader = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                var token = cache.BeginRead();
                cache.Store(Room, "u", token.Epoch.ToString(), token);
                stores++;
            }
        }) { IsBackground = true };
        reader.Start();

        var rnd = new Random(4321);
        for (var r = 0; r < rounds; r++)
        {
            Thread.SpinWait(rnd.Next(0, 400));
            var oldTag = cache.BeginRead().Epoch.ToString();
            if (suspendResume)
            {
                cache.Suspend();
                cache.Resume();
            }
            else
            {
                cache.Flush();
            }

            for (var i = 0; i < 16; i++)
                if (cache.TryGet(Room, "u", out var role) && role == oldTag)
                    failures.Enqueue($"round {r}: pre-invalidation read served after return ({i})");
        }

        Volatile.Write(ref stop, 1);
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)));
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(8)) + $"\ncount={failures.Count}");
        Assert.True(stores > rounds / 2, $"only {stores} stores");
    }
}
