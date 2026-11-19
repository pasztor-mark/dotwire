using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Auth;

public sealed class AccessLookupBatcher(IAccessStore store, IOptions<DotwireOptions> options)
{
    private readonly int _maxInFlight = Math.Max(1, options.Value.AuthBatching.MaxInFlight);
    private readonly int _maxBatchSize = Math.Max(1, options.Value.AuthBatching.MaxBatchSize);
    private readonly Lock _gate = new();
    private readonly Queue<Dictionary<(Guid, string), TaskCompletionSource<(string?, bool)>>> _pending = new();
    private Dictionary<(Guid, string), TaskCompletionSource<(string?, bool)>>? _tail;
    private int _inFlight;

    public Task<(string? Role, bool IsMember)> LookupAsync(Guid roomId, string userId, CancellationToken ct)
    {
        var key = (roomId, userId);
        TaskCompletionSource<(string?, bool)> tcs;
        Dictionary<(Guid, string), TaskCompletionSource<(string?, bool)>>? toRun = null;
        lock (_gate)
        {
            var batch = _tail;
            if (batch is null || (batch.Count >= _maxBatchSize && !batch.ContainsKey(key)))
            {
                batch = new();
                _pending.Enqueue(batch);
                _tail = batch;
            }
            if (!batch.TryGetValue(key, out tcs!))
            {
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                batch[key] = tcs;
            }
            toRun = TryTakeNext();
        }
        if (toRun is not null)
            _ = RunAsync(toRun);
        return tcs.Task.WaitAsync(ct);
    }

    private Dictionary<(Guid, string), TaskCompletionSource<(string?, bool)>>? TryTakeNext()
    {
        if (_inFlight >= _maxInFlight || !_pending.TryDequeue(out var next))
            return null;
        _inFlight++;
        if (_pending.Count == 0)
            _tail = null;
        return next;
    }

    private async Task RunAsync(Dictionary<(Guid, string), TaskCompletionSource<(string?, bool)>> batch)
    {
        try
        {
            if (batch.Count == 1)
            {
                var (key, waiter) = batch.First();
                waiter.SetResult(await store.GetRoleAndMembershipAsync(key.Item1, key.Item2, CancellationToken.None));
            }
            else
            {
                var pairs = batch.Keys.Select(k => (k.Item1, k.Item2)).ToArray();
                var results = await store.GetRolesAndMembershipsAsync(pairs, CancellationToken.None);
                foreach (var (key, waiter) in batch)
                    waiter.SetResult(results.TryGetValue(key, out var r) ? r : (null, false));
            }
        }
        catch (Exception ex)
        {
            foreach (var waiter in batch.Values)
                waiter.TrySetException(ex);
        }
        finally
        {
            Dictionary<(Guid, string), TaskCompletionSource<(string?, bool)>>? next;
            lock (_gate)
            {
                _inFlight--;
                next = TryTakeNext();
            }
            if (next is not null)
                _ = RunAsync(next);
        }
    }
}
