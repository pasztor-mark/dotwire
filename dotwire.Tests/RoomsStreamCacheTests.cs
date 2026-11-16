using System.Reflection;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Xunit;

namespace dotwire.Tests;

public class RoomsStreamCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public class StubStream : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    public class FakeJs : DispatchProxy
    {
        public int GetStreamCalls;
        public int FailFirst;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetStreamAsync")
                throw new NotSupportedException(targetMethod?.Name);
            var call = Interlocked.Increment(ref GetStreamCalls);
            if (call <= FailFirst)
                return ValueTask.FromException<INatsJSStream>(new InvalidOperationException("boom"));
            return ValueTask.FromResult(DispatchProxy.Create<INatsJSStream, StubStream>());
        }
    }

    private static (RoomsStreamCache Cache, FakeJs Js) Build(int failFirst = 0)
    {
        var js = DispatchProxy.Create<INatsJSContext, FakeJs>();
        var fake = (FakeJs)(object)js;
        fake.FailFirst = failFirst;
        return (new RoomsStreamCache(js, Options.Create(new NatsOptions())), fake);
    }

    [Fact]
    public async Task GetAsync_FetchesStreamInfoOnce()
    {
        var (cache, js) = Build();
        Assert.Same(await cache.GetAsync(Ct), await cache.GetAsync(Ct));
        Assert.Equal(1, js.GetStreamCalls);
    }

    [Fact]
    public async Task Invalidate_RefetchesOnNextCall()
    {
        var (cache, js) = Build();
        await cache.GetAsync(Ct);
        cache.Invalidate();
        await cache.GetAsync(Ct);
        Assert.Equal(2, js.GetStreamCalls);
    }

    [Fact]
    public async Task FailedFetch_IsNotCached()
    {
        var (cache, js) = Build(failFirst: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetAsync(Ct));
        await cache.GetAsync(Ct);
        Assert.Equal(2, js.GetStreamCalls);
    }
}
