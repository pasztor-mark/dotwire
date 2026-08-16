using System.Collections.Concurrent;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Dotwire.Realtime;

public sealed class PresenceCoalescer(
    INatsConnection nats,
    IOptions<NatsOptions> natsOptions,
    ILogger<PresenceCoalescer> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<(string UserId, bool Joined)>> _events = new();

    public void QueueJoin(Guid roomId, string userId)
    {
        var queue = _events.GetOrAdd(roomId, _ => new ConcurrentQueue<(string, bool)>());
        queue.Enqueue((userId, true));
    }

    public void QueueLeave(Guid roomId, string userId)
    {
        var queue = _events.GetOrAdd(roomId, _ => new ConcurrentQueue<(string, bool)>());
        queue.Enqueue((userId, false));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Error in presence coalescer flush: {Error}", ex.Message);
            }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (!natsOptions.Value.Enabled)
            return;

        foreach (var (roomId, queue) in _events)
        {
            if (queue.IsEmpty)
            {
                _events.TryRemove(new KeyValuePair<Guid, ConcurrentQueue<(string UserId, bool Joined)>>(roomId, queue));
                continue;
            }

            var transitions = new Dictionary<string, bool>();
            while (queue.TryDequeue(out var item))
            {
                transitions[item.UserId] = item.Joined;
            }

            if (queue.IsEmpty)
            {
                _events.TryRemove(new KeyValuePair<Guid, ConcurrentQueue<(string UserId, bool Joined)>>(roomId, queue));
            }

            var joined = new List<string>();
            var left = new List<string>();
            foreach (var (userId, isJoined) in transitions)
            {
                if (isJoined)
                    joined.Add(userId);
                else
                    left.Add(userId);
            }

            if (joined.Count == 0 && left.Count == 0)
                continue;

            var delta = new PresenceDelta(roomId, joined.ToArray(), left.ToArray());
            var payload = JsonSerializer.SerializeToUtf8Bytes(delta, DotwireJsonContext.Default.PresenceDelta);

            try
            {
                await nats.PublishAsync<byte[]>($"room.{roomId}.ephemeral.presence", payload, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Failed to publish presence delta for room {RoomId}: {Error}", roomId, ex.Message);
            }
        }
    }
}
