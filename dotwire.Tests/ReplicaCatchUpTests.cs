using Dotwire.Data;
using Xunit;

namespace dotwire.Tests;

public class ReplicaCatchUpTests
{
    private sealed class FakeLsnSource(Func<int, ulong?> replica) : ILsnSource
    {
        public int Polls;

        public Task<ulong> PrimaryLsnAsync(CancellationToken ct) => Task.FromResult(100UL);

        public Task<ulong?> ReplicaReplayLsnAsync(CancellationToken ct) => Task.FromResult(replica(Interlocked.Increment(ref Polls)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Primary_ReturnsImmediately()
    {
        var source = new FakeLsnSource(_ => null);

        await ReplicaCatchUp.WaitForReplicaAsync(source, 100, TimeSpan.FromMilliseconds(5), Ct);

        Assert.Equal(1, source.Polls);
    }

    [Fact]
    public async Task Replica_Behind_WaitsUntilCaughtUp()
    {
        var source = new FakeLsnSource(poll => poll >= 3 ? 100UL : 40UL);

        await ReplicaCatchUp.WaitForReplicaAsync(source, 100, TimeSpan.FromMilliseconds(5), Ct);

        Assert.True(source.Polls >= 3);
    }

    [Fact]
    public async Task Replica_NeverCatchesUp_NeverReturns()
    {
        var source = new FakeLsnSource(_ => 40UL);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReplicaCatchUp.WaitForReplicaAsync(source, 100, TimeSpan.FromMilliseconds(5), cts.Token));
    }

    [Fact]
    public async Task Replica_Behind_ReportsStillWaiting()
    {
        var source = new FakeLsnSource(poll => poll >= 5 ? 100UL : 40UL);
        var reports = new List<ulong>();

        await ReplicaCatchUp.WaitForReplicaAsync(source, 100, TimeSpan.FromMilliseconds(5), Ct,
            (_, replayed) => reports.Add(replayed), TimeSpan.Zero);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(40UL, r));
    }

    [Fact]
    public async Task Replica_CaughtUp_NeverReportsStillWaiting()
    {
        var source = new FakeLsnSource(_ => 100UL);
        var reports = 0;

        await ReplicaCatchUp.WaitForReplicaAsync(source, 100, TimeSpan.FromMilliseconds(5), Ct,
            (_, _) => reports++, TimeSpan.Zero);

        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        var source = new FakeLsnSource(_ => 40UL);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReplicaCatchUp.WaitForReplicaAsync(source, 100, TimeSpan.FromMilliseconds(5), cts.Token));
    }
}
