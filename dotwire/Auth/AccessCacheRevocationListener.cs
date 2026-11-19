using System.Text.Json;
using Dotwire.Configuration;
using Dotwire.Api;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Dotwire.Auth;

public sealed class AccessCacheRevocationListener(
    INatsConnection nats,
    AccessCache cache,
    IOptions<NatsOptions> natsOptions,
    IOptions<DotwireOptions> options,
    ILogger<AccessCacheRevocationListener> logger) : BackgroundService
{
    private const string Subject = "room.*.ephemeral.revoked";
    private volatile INatsSub<byte[]>? _sub;
    private readonly object _gate = new();
    private volatile bool _running;
    private long _disconnects;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!natsOptions.Value.Enabled)
            return;

        nats.ConnectionDisconnected += OnDisconnected;
        nats.ConnectionOpened += OnOpened;
        nats.MessageDropped += OnDropped;
        nats.SlowConsumerDetected += OnSlow;

        var sweep = SweepAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var disconnectsBefore = Volatile.Read(ref _disconnects);
                    await using var sub = await nats.SubscribeCoreAsync<byte[]>(Subject, cancellationToken: stoppingToken);
                    _sub = sub;
                    await nats.PingAsync(stoppingToken);
                    lock (_gate)
                    {
                        if (_disconnects != disconnectsBefore)
                            throw new InvalidOperationException("connection dropped while subscribing");
                        _running = true;
                        if (nats.ConnectionState == NatsConnectionState.Open)
                            cache.Resume();
                    }

                    await foreach (var msg in sub.Msgs.ReadAllAsync(stoppingToken))
                        Handle(msg.Data);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Access cache revocation subscription failed, retrying: {Error}", ex.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _running = false;
                        _sub = null;
                        cache.Suspend();
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            Unsubscribe();
            try { await sweep; } catch (OperationCanceledException) { }
        }
    }

    private void Handle(byte[]? data)
    {
        try
        {
            var revoked = data is null ? null : JsonSerializer.Deserialize(data, DotwireJsonContext.Default.MembershipRevoked);
            if (revoked is null || revoked.UserId is null || revoked.RoomId == Guid.Empty)
            {
                cache.Flush();
                logger.LogWarning("Unparseable revocation message; access cache flushed");
                return;
            }

            cache.Evict(revoked.RoomId, revoked.UserId);
        }
        catch (JsonException)
        {
            cache.Flush();
            logger.LogWarning("Unparseable revocation message; access cache flushed");
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.AuthCacheSeconds));
        while (await timer.WaitForNextTickAsync(ct))
            cache.RemoveExpired();
    }

    private ValueTask OnDisconnected(object? sender, NatsEventArgs args)
    {
        lock (_gate)
        {
            _disconnects++;
            cache.Suspend();
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnOpened(object? sender, NatsEventArgs args)
    {
        if (!_running)
            return;
        try
        {
            await nats.PingAsync();
            lock (_gate)
            {
                if (_running && nats.ConnectionState == NatsConnectionState.Open)
                {
                    cache.Flush();
                    cache.Resume();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Access cache resume ping failed: {Error}", ex.Message);
        }
    }

    private ValueTask OnDropped(object? sender, NatsMessageDroppedEventArgs args)
    {
        if (ReferenceEquals(args.Subscription, _sub))
            cache.Flush();
        return ValueTask.CompletedTask;
    }

    private ValueTask OnSlow(object? sender, NatsSlowConsumerEventArgs args)
    {
        if (ReferenceEquals(args.Subscription, _sub))
            cache.Flush();
        return ValueTask.CompletedTask;
    }

    private void Unsubscribe()
    {
        nats.ConnectionDisconnected -= OnDisconnected;
        nats.ConnectionOpened -= OnOpened;
        nats.MessageDropped -= OnDropped;
        nats.SlowConsumerDetected -= OnSlow;
    }

    public override void Dispose()
    {
        if (natsOptions.Value.Enabled)
            Unsubscribe();
        base.Dispose();
    }
}
