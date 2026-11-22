using Dotwire.Auth;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class AccessLookupBatcherTests
{
    private static readonly Guid Room = Guid.NewGuid();

    private static (Guid, string) P(string user) => (Room, user);

    private static AccessLookupBatcher NewBatcher(GatedAccessStore store, int maxInFlight = 2, int maxBatchSize = 512, DotwireMetrics? metrics = null) =>
        new(store, Options.Create(new DotwireOptions
        {
            AuthBatching = new AuthBatchingOptions { Enabled = true, MaxInFlight = maxInFlight, MaxBatchSize = maxBatchSize }
        }), metrics ?? TestMetrics.Create().Metrics);

    [Fact]
    public async Task LoneRequest_UsesSingleLookup()
    {
        var store = new GatedAccessStore();
        store.Roles["a"] = "admin";
        store.Members[P("a")] = true;
        var batcher = NewBatcher(store);

        var task = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        store.Calls[0].Gate.SetResult();

        Assert.Equal(("admin", true), await task);
        Assert.Equal(1, store.SingleCount);
        Assert.Equal(0, store.BatchCount);
    }

    [Fact]
    public async Task RequestsWhileSlotsBusy_GoInOneFollowUpBatch()
    {
        var store = new GatedAccessStore();
        foreach (var u in new[] { "a", "b", "c", "d" })
        {
            store.Roles[u] = "r-" + u;
            store.Members[P(u)] = u != "c";
        }
        var (metrics, scope) = TestMetrics.Create();
        using var capture = new MetricCapture(scope);
        var batcher = NewBatcher(store, maxInFlight: 1, metrics: metrics);

        var a = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        var b = batcher.LookupAsync(Room, "b", CancellationToken.None);
        var c = batcher.LookupAsync(Room, "c", CancellationToken.None);
        var d = batcher.LookupAsync(Room, "d", CancellationToken.None);
        store.Calls[0].Gate.SetResult();
        await store.WaitForCallsAsync(2);
        store.Calls[1].Gate.SetResult();

        Assert.Equal(("r-a", true), await a);
        Assert.Equal(("r-b", true), await b);
        Assert.Equal(("r-c", false), await c);
        Assert.Equal(("r-d", true), await d);
        Assert.Equal(2, store.Calls.Count);
        Assert.True(store.Calls[1].IsBatch);
        Assert.Equal(new[] { P("b"), P("c"), P("d") }.Order(), store.Calls[1].Pairs.Order());
        Assert.Equal(4, capture.Sum("dotwire.auth.batch.size"));
    }

    [Fact]
    public async Task RequestAfterDispatch_IsNotInThatBatch()
    {
        var store = new GatedAccessStore();
        foreach (var u in new[] { "a", "b", "c" })
            store.Roles[u] = "r-" + u;
        var batcher = NewBatcher(store, maxInFlight: 1);

        var a = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        var b = batcher.LookupAsync(Room, "b", CancellationToken.None);
        store.Calls[0].Gate.SetResult();
        await store.WaitForCallsAsync(2);
        var c = batcher.LookupAsync(Room, "c", CancellationToken.None);

        Assert.Equal(new[] { P("b") }, store.Calls[1].Pairs);
        Assert.Equal(2, store.Calls.Count);
        store.Calls[1].Gate.SetResult();
        await store.WaitForCallsAsync(3);
        Assert.Equal(new[] { P("c") }, store.Calls[2].Pairs);
        store.Calls[2].Gate.SetResult();

        Assert.Equal(("r-b", false), await b);
        Assert.Equal(("r-c", false), await c);
        await a;
    }

    [Fact]
    public async Task DuplicatePairsInPendingBatch_ShareOneResult()
    {
        var store = new GatedAccessStore();
        store.Roles["b"] = "r-b";
        var batcher = NewBatcher(store, maxInFlight: 1);

        var a = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        var b1 = batcher.LookupAsync(Room, "b", CancellationToken.None);
        var b2 = batcher.LookupAsync(Room, "b", CancellationToken.None);
        var b3 = batcher.LookupAsync(Room, "b", CancellationToken.None);
        store.Calls[0].Gate.SetResult();
        await store.WaitForCallsAsync(2);
        store.Calls[1].Gate.SetResult();

        Assert.Equal(new[] { P("b") }, store.Calls[1].Pairs);
        Assert.Equal(("r-b", false), await b1);
        Assert.Equal(("r-b", false), await b2);
        Assert.Equal(("r-b", false), await b3);
        Assert.Equal(2, store.Calls.Count);
        await a;
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelOthers()
    {
        var store = new GatedAccessStore();
        store.Roles["c"] = "r-c";
        var batcher = NewBatcher(store, maxInFlight: 1);
        using var cts = new CancellationTokenSource();

        var a = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        var b = batcher.LookupAsync(Room, "b", cts.Token);
        var c = batcher.LookupAsync(Room, "c", CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        store.Calls[0].Gate.SetResult();
        await store.WaitForCallsAsync(2);
        store.Calls[1].Gate.SetResult();

        Assert.Equal(("r-c", false), await c);
        await a;
    }

    [Fact]
    public async Task StoreException_ReachesAllWaitersAndFreesSlot()
    {
        var store = new GatedAccessStore();
        store.Roles["d"] = "r-d";
        var batcher = NewBatcher(store, maxInFlight: 1);

        var a = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        var b = batcher.LookupAsync(Room, "b", CancellationToken.None);
        var c = batcher.LookupAsync(Room, "c", CancellationToken.None);
        store.Calls[0].Gate.SetResult();
        await store.WaitForCallsAsync(2);
        store.Calls[1].Gate.SetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c);
        await a;

        var d = batcher.LookupAsync(Room, "d", CancellationToken.None);
        await store.WaitForCallsAsync(3);
        store.Calls[2].Gate.SetResult();
        Assert.Equal(("r-d", false), await d);
    }

    [Fact]
    public async Task BatchOverflow_SplitsAtMaxBatchSize()
    {
        var store = new GatedAccessStore();
        var batcher = NewBatcher(store, maxInFlight: 1, maxBatchSize: 2);

        var a = batcher.LookupAsync(Room, "a", CancellationToken.None);
        await store.WaitForCallsAsync(1);
        var b = batcher.LookupAsync(Room, "b", CancellationToken.None);
        var c = batcher.LookupAsync(Room, "c", CancellationToken.None);
        var d = batcher.LookupAsync(Room, "d", CancellationToken.None);
        store.Calls[0].Gate.SetResult();
        await store.WaitForCallsAsync(2);
        store.Calls[1].Gate.SetResult();
        await store.WaitForCallsAsync(3);
        store.Calls[2].Gate.SetResult();
        await Task.WhenAll(a, b, c, d);

        Assert.Equal(new[] { P("b"), P("c") }.Order(), store.Calls[1].Pairs.Order());
        Assert.Equal(new[] { P("d") }, store.Calls[2].Pairs);
    }

    [Theory]
    [InlineData(1, 512, true)]
    [InlineData(2, 512, true)]
    [InlineData(4, 8, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 512, false)]
    [InlineData(1, 1, false)]
    public async Task Stress_ManyConcurrentLookups_MatchTruthAndRespectMaxInFlight(int maxInFlight, int maxBatchSize, bool yields)
    {
        var rooms = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var pairs = rooms.SelectMany(r => Enumerable.Range(0, 10).Select(u => (r, "u" + u))).ToArray();
        var store = new CountingAccessStore(yields);
        for (var u = 0; u < 10; u++)
            if (u % 3 != 0)
                store.Roles["u" + u] = u % 2 == 0 ? "admin" : "member";
        for (var i = 0; i < pairs.Length; i++)
            if (i % 4 != 0)
                store.Members[pairs[i]] = true;
        var batcher = new AccessLookupBatcher(store, Options.Create(new DotwireOptions
        {
            AuthBatching = new AuthBatchingOptions { Enabled = true, MaxInFlight = maxInFlight, MaxBatchSize = maxBatchSize }
        }), TestMetrics.Create().Metrics);

        var lookups = Enumerable.Range(0, 1000).Select(i =>
        {
            var (room, user) = pairs[i % pairs.Length];
            return Task.Run(async () => (room, user, result: await batcher.LookupAsync(room, user, CancellationToken.None)));
        }).ToArray();
        var results = await Task.WhenAll(lookups).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        foreach (var (room, user, result) in results)
            Assert.Equal(store.Truth(room, user), result);
        Assert.InRange(store.MaxConcurrent, 1, maxInFlight);
        Assert.True(store.TotalCalls <= 1000);
    }

    [Fact]
    public async Task FakeAccessStore_BatchMethod_ReturnsEveryPair()
    {
        var store = new FakeAccessStore();
        store.Roles["member"] = "member";
        store.Roles["outsider"] = "auditor";
        store.Members[(Room, "member")] = true;

        var result = await store.GetRolesAndMembershipsAsync(
            [(Room, "member"), (Room, "outsider"), (Room, "nobody")], CancellationToken.None);

        Assert.Equal(("member", true), result[(Room, "member")]);
        Assert.Equal(("auditor", false), result[(Room, "outsider")]);
        Assert.Equal((null, false), result[(Room, "nobody")]);
        Assert.Equal(1, store.BatchChecks);
    }
}

public sealed class CountingAccessStore(bool yields) : IAccessStore
{
    private int _current;
    private int _max;
    private int _total;

    public Dictionary<string, string> Roles { get; } = new();
    public Dictionary<(Guid, string), bool> Members { get; } = new();
    public int MaxConcurrent => Volatile.Read(ref _max);
    public int TotalCalls => Volatile.Read(ref _total);

    public (string?, bool) Truth(Guid roomId, string userId) =>
        (Roles.GetValueOrDefault(userId), Members.GetValueOrDefault((roomId, userId)));

    private void Enter()
    {
        Interlocked.Increment(ref _total);
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
    }

    public async Task<(string? Role, bool IsMember)> GetRoleAndMembershipAsync(Guid roomId, string userId, CancellationToken ct)
    {
        Enter();
        try
        {
            if (yields)
                await Task.Yield();
            return Truth(roomId, userId);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    public async Task<IReadOnlyDictionary<(Guid RoomId, string UserId), (string? Role, bool IsMember)>> GetRolesAndMembershipsAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct)
    {
        Enter();
        try
        {
            if (yields)
                await Task.Yield();
            return pairs.ToDictionary(p => p, p => Truth(p.RoomId, p.UserId));
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    public Task<string?> GetRoleAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> IsMemberAsync(Guid roomId, string userId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> GetRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlySet<(Guid RoomId, string UserId)>> FilterMembersAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class GatedAccessStore : IAccessStore
{
    public sealed record Call(bool IsBatch, (Guid RoomId, string UserId)[] Pairs, TaskCompletionSource Gate);

    private readonly Lock _lock = new();
    private readonly List<Call> _calls = [];
    private readonly SemaphoreSlim _arrived = new(0);

    public Dictionary<string, string> Roles { get; } = new();
    public Dictionary<(Guid, string), bool> Members { get; } = new();

    public IReadOnlyList<Call> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    public int SingleCount => Calls.Count(c => !c.IsBatch);
    public int BatchCount => Calls.Count(c => c.IsBatch);

    public async Task WaitForCallsAsync(int count)
    {
        while (Calls.Count < count)
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10)), "timed out waiting for store call");
    }

    private Call Record(bool isBatch, (Guid, string)[] pairs)
    {
        var call = new Call(isBatch, pairs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_lock) _calls.Add(call);
        _arrived.Release();
        return call;
    }

    private (string?, bool) Truth(Guid roomId, string userId) =>
        (Roles.GetValueOrDefault(userId), Members.GetValueOrDefault((roomId, userId)));

    public async Task<(string? Role, bool IsMember)> GetRoleAndMembershipAsync(Guid roomId, string userId, CancellationToken ct)
    {
        await Record(false, [(roomId, userId)]).Gate.Task;
        return Truth(roomId, userId);
    }

    public async Task<IReadOnlyDictionary<(Guid RoomId, string UserId), (string? Role, bool IsMember)>> GetRolesAndMembershipsAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct)
    {
        await Record(true, pairs.ToArray()).Gate.Task;
        return pairs.ToDictionary(p => p, p => Truth(p.RoomId, p.UserId));
    }

    public Task<string?> GetRoleAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> IsMemberAsync(Guid roomId, string userId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> GetRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlySet<(Guid RoomId, string UserId)>> FilterMembersAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct) => throw new NotSupportedException();
}
