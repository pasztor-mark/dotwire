using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Dotwire.Realtime;

/// <summary>One pending SSE wire event (spec §3.9). <c>Id</c> is set only for `message` events.</summary>
public readonly record struct SseOutboundEvent(string EventName, byte[] Data, ulong? Id);

/// <summary>
/// The single owner of per-room fanout on a node (spec §3.9). Tracks, per connection, which
/// rooms it's subscribed to and as which user - moved here from <see cref="RoomHub"/>'s
/// <c>Context.Items</c> so membership revocation (spec §3.5) can find and drop a connection
/// without going through the hub. Fanout to SignalR is still one group-send per event; the
/// per-connection map exists for revocation/<see cref="IsSubscribed"/>, not to fan out
/// per-connection.
/// </summary>
public sealed class RoomInterestManager : IAsyncDisposable
{
    private sealed record RoomSubscription(
        Dictionary<string, string> ConnectionUsers,
        CancellationTokenSource Cts,
        Task MessageTask,
        Task EphemeralTask);

    private sealed class ConnectionInfo(string userId)
    {
        public string UserId { get; } = userId;
        public HashSet<Guid> Rooms { get; } = [];
    }

    private readonly INatsConnection _nats;
    private readonly IHubContext<RoomHub> _hubContext;
    private readonly MessageCipher _cipher;
    private readonly PresenceCoalescer _presenceCoalescer;
    private readonly NatsOptions _natsOptions;
    private readonly ILogger<RoomInterestManager> _logger;
    private readonly ConcurrentDictionary<Guid, RoomSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Channel<SseOutboundEvent>>> _sseChannels = new();
    private readonly Lock _gate = new();

    public RoomInterestManager(
        INatsConnection nats,
        IHubContext<RoomHub> hubContext,
        MessageCipher cipher,
        PresenceCoalescer presenceCoalescer,
        IOptions<NatsOptions> natsOptions,
        ILogger<RoomInterestManager> logger)
    {
        _nats = nats;
        _hubContext = hubContext;
        _cipher = cipher;
        _presenceCoalescer = presenceCoalescer;
        _natsOptions = natsOptions.Value;
        _logger = logger;
    }

    /// <summary>Returns false if the connection was already subscribed to this room.</summary>
    public bool Subscribe(Guid roomId, string connectionId, string userId)
    {
        lock (_gate)
        {
            var info = _connections.GetOrAdd(connectionId, _ => new ConnectionInfo(userId));
            if (!info.Rooms.Add(roomId))
                return false;

            if (_natsOptions.Enabled)
            {
                var subscription = _subscriptions.GetOrAdd(roomId, CreateSubscription);
                subscription.ConnectionUsers[connectionId] = userId;
            }

            return true;
        }
    }

    /// <summary>Returns false if the connection wasn't subscribed to this room.</summary>
    public bool Unsubscribe(Guid roomId, string connectionId)
    {
        RoomSubscription? toDispose = null;
        bool removed;
        lock (_gate)
        {
            removed = _connections.TryGetValue(connectionId, out var info) && info.Rooms.Remove(roomId);
            if (removed && info!.Rooms.Count == 0)
                _connections.TryRemove(connectionId, out _);

            if (removed && _natsOptions.Enabled && _subscriptions.TryGetValue(roomId, out var existing))
            {
                existing.ConnectionUsers.Remove(connectionId);
                if (existing.ConnectionUsers.Count == 0)
                    _subscriptions.TryRemove(roomId, out toDispose);
            }
        }

        toDispose?.Cts.Cancel();
        return removed;
    }

    /// <summary>Drops a disconnected connection from every room it was subscribed to; returns those room ids.</summary>
    public IReadOnlyList<Guid> DisconnectAll(string connectionId)
    {
        List<Guid> rooms;
        lock (_gate)
        {
            if (!_connections.TryRemove(connectionId, out var info))
                return [];
            rooms = [.. info.Rooms];
        }

        foreach (var roomId in rooms)
        {
            RoomSubscription? toDispose = null;
            lock (_gate)
            {
                if (_natsOptions.Enabled && _subscriptions.TryGetValue(roomId, out var existing))
                {
                    existing.ConnectionUsers.Remove(connectionId);
                    if (existing.ConnectionUsers.Count == 0)
                        _subscriptions.TryRemove(roomId, out toDispose);
                }
            }
            toDispose?.Cts.Cancel();
        }

        return rooms;
    }

    /// <summary>
    /// Registers an SSE connection as a room subscriber (spec §3.9) and returns the reader it
    /// should forward to the response body. A synthetic connection id keeps it in the same
    /// bookkeeping as hub connections, so <see cref="RevokeAsync"/> can find and close it too.
    /// </summary>
    public ChannelReader<SseOutboundEvent> RegisterSseConnection(Guid roomId, string connectionId, string userId, int bufferSize)
    {
        var channel = Channel.CreateBounded<SseOutboundEvent>(new BoundedChannelOptions(bufferSize)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        var perRoom = _sseChannels.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, Channel<SseOutboundEvent>>());
        perRoom[connectionId] = channel;
        Subscribe(roomId, connectionId, userId);
        return channel.Reader;
    }

    /// <summary>Tears down an SSE connection's registration; safe to call more than once.</summary>
    public void UnregisterSseConnection(Guid roomId, string connectionId)
    {
        if (_sseChannels.TryGetValue(roomId, out var perRoom))
        {
            if (perRoom.TryRemove(connectionId, out var channel))
                channel.Writer.TryComplete();
            if (perRoom.IsEmpty)
                _sseChannels.TryRemove(roomId, out _);
        }

        Unsubscribe(roomId, connectionId);
    }

    /// <summary>
    /// Fans an event out to every SSE connection registered for <paramref name="roomId"/>.
    /// A full channel disconnects the laggard (spec §3.9): the write is dropped and the
    /// channel completed, closing that connection so the client resumes with `Last-Event-ID`.
    /// </summary>
    private void PublishToSse(Guid roomId, SseOutboundEvent evt)
    {
        if (!_sseChannels.TryGetValue(roomId, out var perRoom))
            return;

        foreach (var (connectionId, channel) in perRoom)
        {
            if (!channel.Writer.TryWrite(evt))
            {
                channel.Writer.TryComplete();
                perRoom.TryRemove(connectionId, out _);
            }
        }
    }

    public bool IsSubscribed(string connectionId, Guid roomId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out var info) && info.Rooms.Contains(roomId);
        }
    }

    /// <summary>
    /// Drops every local connection subscribed to <paramref name="roomId"/> as
    /// <paramref name="userId"/>: removes it from the SignalR group and its own bookkeeping,
    /// sends it <c>MembershipRevoked</c>, and queues a presence leave (spec §3.5).
    /// </summary>
    public async Task RevokeAsync(Guid roomId, string userId, CancellationToken ct = default)
    {
        List<string> affected = [];
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(roomId, out var subscription))
            {
                foreach (var (connectionId, subscribedUserId) in subscription.ConnectionUsers)
                {
                    if (subscribedUserId == userId)
                        affected.Add(connectionId);
                }
            }
        }
        foreach (var connectionId in affected)
        {
            // Notify first, with a token independent of this room's subscription: dropping
            // the last subscriber below cancels the room's own CTS (the very `ct` this loop
            // iteration runs on), which would otherwise cancel the notification too.
            try
            {
                await _hubContext.Groups.RemoveFromGroupAsync(connectionId, roomId.ToString(), CancellationToken.None);
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("MembershipRevoked", new MembershipRevoked(roomId, userId), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Failed to notify connection {ConnectionId} of revocation: {Error}", connectionId, ex.Message);
            }

            // SSE: send `revoked`, then close the stream (spec §3.5/§3.9's "server closes the stream").
            if (_sseChannels.TryGetValue(roomId, out var perRoom) && perRoom.TryRemove(connectionId, out var channel))
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new SseRevokedPayload(roomId), DotwireJsonContext.Default.SseRevokedPayload);
                channel.Writer.TryWrite(new SseOutboundEvent("revoked", payload, null));
                channel.Writer.TryComplete();
            }

            Unsubscribe(roomId, connectionId);
            _presenceCoalescer.QueueLeave(roomId, userId);
        }
    }

    private RoomSubscription CreateSubscription(Guid roomId)
    {
        var cts = new CancellationTokenSource();
        var messageTask = Task.Run(() => SubscribeMessagesLoopAsync(roomId, cts.Token));
        var ephemeralTask = Task.Run(() => SubscribeEphemeralLoopAsync(roomId, cts.Token));
        return new RoomSubscription(new Dictionary<string, string>(), cts, messageTask, ephemeralTask);
    }

    private async Task SubscribeMessagesLoopAsync(Guid roomId, CancellationToken ct)
    {
        var roomIdStr = roomId.ToString();
        // Live fanout rides NATS core, not JetStream (spec §3.1/§3.2): the send path
        // republishes each acked message here with its stream seq attached.
        var subject = $"{_natsOptions.RoomSubjectPrefix}{roomId}{_natsOptions.RoomLiveSubjectSuffix}";
        try
        {
            await foreach (var msg in _nats.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
            {
                if (msg.Data is null)
                    continue;

                try
                {
                    var roomMessage = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.RoomLiveMessage);
                    if (roomMessage is null)
                        continue;

                    var decrypted = _cipher.Decrypt(roomMessage.KeyId, roomMessage.Content);
                    var plaintext = Encoding.UTF8.GetString(decrypted);
                    var delivery = new RoomMessageDelivery(roomMessage.RoomId, roomMessage.Seq, roomMessage.SenderId, roomMessage.Time, plaintext);

                    await _hubContext.Clients.Group(roomIdStr).SendAsync("ReceiveMessage", delivery, ct);

                    var ssePayload = new SseMessagePayload(roomMessage.RoomId, roomMessage.Seq, roomMessage.SenderId, roomMessage.Time, plaintext, Replayed: false);
                    var sseData = JsonSerializer.SerializeToUtf8Bytes(ssePayload, DotwireJsonContext.Default.SseMessagePayload);
                    PublishToSse(roomId, new SseOutboundEvent("message", sseData, roomMessage.Seq));
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
                            PublishToSse(roomId, new SseOutboundEvent("typing", msg.Data, null));
                        }
                    }
                    else if (msg.Subject.EndsWith(".presence", StringComparison.Ordinal))
                    {
                        var presence = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.PresenceDelta);
                        if (presence is not null)
                        {
                            await _hubContext.Clients.Group(roomIdStr).SendAsync("PresenceUpdated", presence, ct);
                            PublishToSse(roomId, new SseOutboundEvent("presence", msg.Data, null));
                        }
                    }
                    else if (msg.Subject.EndsWith(".retracted", StringComparison.Ordinal))
                    {
                        var retracted = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.MessageRetracted);
                        if (retracted is not null)
                        {
                            await _hubContext.Clients.Group(roomIdStr).SendAsync("MessageRetracted", retracted, ct);
                            PublishToSse(roomId, new SseOutboundEvent("retracted", msg.Data, null));
                        }
                    }
                    else if (msg.Subject.EndsWith(".revoked", StringComparison.Ordinal))
                    {
                        var revoked = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.MembershipRevoked);
                        if (revoked is not null)
                        {
                            await RevokeAsync(revoked.RoomId, revoked.UserId, ct);
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
