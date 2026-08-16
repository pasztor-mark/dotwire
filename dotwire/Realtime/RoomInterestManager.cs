using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Dotwire.Realtime;

public sealed class RoomInterestManager : IAsyncDisposable
{
    private sealed record RoomSubscription(
        HashSet<string> Connections,
        CancellationTokenSource Cts,
        Task MessageTask,
        Task EphemeralTask);

    private readonly INatsConnection _nats;
    private readonly IHubContext<RoomHub> _hubContext;
    private readonly MessageCipher _cipher;
    private readonly NatsOptions _natsOptions;
    private readonly ILogger<RoomInterestManager> _logger;
    private readonly ConcurrentDictionary<Guid, RoomSubscription> _subscriptions = new();
    private readonly Lock _gate = new();

    public RoomInterestManager(
        INatsConnection nats,
        IHubContext<RoomHub> hubContext,
        MessageCipher cipher,
        IOptions<NatsOptions> natsOptions,
        ILogger<RoomInterestManager> logger)
    {
        _nats = nats;
        _hubContext = hubContext;
        _cipher = cipher;
        _natsOptions = natsOptions.Value;
        _logger = logger;
    }

    public void RegisterInterest(Guid roomId, string connectionId)
    {
        if (!_natsOptions.Enabled)
            return;

        lock (_gate)
        {
            if (_subscriptions.TryGetValue(roomId, out var existing))
            {
                existing.Connections.Add(connectionId);
                return;
            }

            var cts = new CancellationTokenSource();
            var connections = new HashSet<string> { connectionId };
            var messageTask = Task.Run(() => SubscribeMessagesLoopAsync(roomId, cts.Token));
            var ephemeralTask = Task.Run(() => SubscribeEphemeralLoopAsync(roomId, cts.Token));

            _subscriptions[roomId] = new RoomSubscription(connections, cts, messageTask, ephemeralTask);
        }
    }

    public void UnregisterInterest(Guid roomId, string connectionId)
    {
        if (!_natsOptions.Enabled)
            return;

        RoomSubscription? toDispose = null;
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(roomId, out var existing))
            {
                existing.Connections.Remove(connectionId);
                if (existing.Connections.Count == 0)
                {
                    _subscriptions.TryRemove(roomId, out toDispose);
                }
            }
        }

        if (toDispose is not null)
        {
            toDispose.Cts.Cancel();
        }
    }

    private async Task SubscribeMessagesLoopAsync(Guid roomId, CancellationToken ct)
    {
        var roomIdStr = roomId.ToString();
        var subject = $"{_natsOptions.RoomSubjectPrefix}{roomId}";
        try
        {
            await foreach (var msg in _nats.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
            {
                if (msg.Data is null)
                    continue;

                try
                {
                    var roomMessage = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.RoomMessage);
                    if (roomMessage is null)
                        continue;

                    var decrypted = _cipher.Decrypt(roomMessage.KeyId, roomMessage.Content);
                    var plaintext = Encoding.UTF8.GetString(decrypted);
                    var delivery = new RoomMessageDelivery(roomMessage.RoomId, roomMessage.SenderId, roomMessage.Time, plaintext);

                    await _hubContext.Clients.Group(roomIdStr).SendAsync("ReceiveMessage", delivery, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Failed to process message fanout for room {RoomId}: {Error}", roomId, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError("Message subscription loop error for room {RoomId}: {Error}", roomId, ex.Message);
        }
    }

    private async Task SubscribeEphemeralLoopAsync(Guid roomId, CancellationToken ct)
    {
        var roomIdStr = roomId.ToString();
        var subject = $"room.{roomId}.ephemeral.>";
        try
        {
            await foreach (var msg in _nats.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
            {
                if (msg.Data is null)
                    continue;

                try
                {
                    if (msg.Subject.EndsWith(".typing", StringComparison.Ordinal))
                    {
                        var typing = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.TypingNotification);
                        if (typing is not null)
                        {
                            await _hubContext.Clients.Group(roomIdStr).SendAsync("UserTyping", typing, ct);
                        }
                    }
                    else if (msg.Subject.EndsWith(".presence", StringComparison.Ordinal))
                    {
                        var presence = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.PresenceDelta);
                        if (presence is not null)
                        {
                            await _hubContext.Clients.Group(roomIdStr).SendAsync("PresenceUpdated", presence, ct);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Failed to process ephemeral event for room {RoomId}: {Error}", roomId, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError("Ephemeral subscription loop error for room {RoomId}: {Error}", roomId, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, sub) in _subscriptions)
        {
            sub.Cts.Cancel();
            try
            {
                await Task.WhenAll(sub.MessageTask, sub.EphemeralTask);
            }
            catch
            {
            }
            sub.Cts.Dispose();
        }
        _subscriptions.Clear();
    }
}
