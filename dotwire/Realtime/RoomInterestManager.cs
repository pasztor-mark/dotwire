using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Dotwire.Realtime;

/// <summary>One pending SSE wire event (spec §3.9). <c>Id</c> is set only for `message` events.</summary>
public readonly record struct SseOutboundEvent(string EventName, byte[] Data, ulong? Id);

/// <summary>
/// The single owner of per-room fanout on a node (spec §3.9). Tracks, per connection, which
/// rooms it's subscribed to and as which user - moved here from <see cref="RoomHub"/>'s
/// <c>Context.Items</c> so membership revocation (spec §3.5) can find and drop a connection
/// without going through the hub.
/// </summary>
public sealed class RoomInterestManager : IAsyncDisposable
{
    private const int HubSendQueueCapacity = 256;

    private sealed record RoomSubscription(
        ConcurrentDictionary<string, ConnectionInfo> Connections,
        CancellationTokenSource Cts,
        Task Ready,
        Task MessageTask,
        Task EphemeralTask);

    private sealed class ConnectionInfo(string userId, HubOutbox? outbox)
    {
        public string UserId { get; } = userId;
        public HubOutbox? Outbox { get; } = outbox;
        public HashSet<Guid> Rooms { get; } = [];
    }

    private sealed record SseConnection(Channel<SseOutboundEvent> Channel, CancellationTokenSource Revoked);

    private sealed class HubOutbox
    {
        private readonly HubConnectionContext _connection;
        private readonly Channel<SerializedHubMessage> _channel = Channel.CreateBounded<SerializedHubMessage>(
            new BoundedChannelOptions(HubSendQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        private volatile bool _closed;

        public HubOutbox(HubConnectionContext connection)
        {
            _connection = connection;
            _ = Task.Run(PumpAsync);
        }

        public void Post(SerializedHubMessage message)
        {
            if (_channel.Writer.TryWrite(message) || _closed)
                return;

            _closed = true;
            _channel.Writer.TryComplete();
            _connection.Abort();
        }

        public void Complete()
        {
            _closed = true;
            _channel.Writer.TryComplete();
        }

        private async Task PumpAsync()
        {
            var ct = _connection.ConnectionAborted;
            try
            {
                await foreach (var message in _channel.Reader.ReadAllAsync(ct))
                    await _connection.WriteAsync(message, ct);
            }
            catch
            {
            }
        }
    }

    private readonly INatsConnection _nats;
    private readonly RoomHubLifetimeManager _lifetimeManager;
    private readonly MessageCipher _cipher;
    private readonly PresenceCoalescer _presenceCoalescer;
    private readonly NatsOptions _natsOptions;
    private readonly ILogger<RoomInterestManager> _logger;
    private readonly ConcurrentDictionary<Guid, RoomSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, SseConnection>> _sseChannels = new();
    private readonly Lock _gate = new();

    public RoomInterestManager(
        INatsConnection nats,
        RoomHubLifetimeManager lifetimeManager,
        MessageCipher cipher,
        PresenceCoalescer presenceCoalescer,
        IOptions<NatsOptions> natsOptions,
        ILogger<RoomInterestManager> logger)
    {
        _nats = nats;
        _lifetimeManager = lifetimeManager;
        _cipher = cipher;
        _presenceCoalescer = presenceCoalescer;
        _natsOptions = natsOptions.Value;
        _logger = logger;
    }

    /// <summary>Returns false if the connection was already subscribed to this room.</summary>
    public async Task<bool> SubscribeAsync(Guid roomId, string connectionId, string userId)
    {
        Task ready;
        lock (_gate)
        {
            var info = _connections.GetOrAdd(connectionId, id => new ConnectionInfo(userId, CreateOutbox(id)));
            if (!info.Rooms.Add(roomId))
                return false;

            if (!_natsOptions.Enabled)
                return true;

            var subscription = _subscriptions.GetOrAdd(roomId, CreateSubscription);
            subscription.Connections[connectionId] = info;
            ready = subscription.Ready;
        }

        try
        {
            await ready;
        }
        catch
        {
            Unsubscribe(roomId, connectionId);
            throw;
        }

        return true;
    }

    /// <summary>Returns false if the connection wasn't subscribed to this room.</summary>
    public bool Unsubscribe(Guid roomId, string connectionId)
    {
        RoomSubscription? toDispose = null;
        bool removed;
        lock (_gate)
        {
            removed = _connections.TryGetValue(connectionId, out var info) && info.Rooms.Remove(roomId);
            if (removed && info!.Rooms.Count == 0 && info.Outbox is null)
                _connections.TryRemove(connectionId, out _);

            if (removed && _natsOptions.Enabled && _subscriptions.TryGetValue(roomId, out var existing))
            {
                existing.Connections.TryRemove(connectionId, out _);
                if (existing.Connections.IsEmpty)
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
            info.Outbox?.Complete();
            rooms = [.. info.Rooms];
        }

        foreach (var roomId in rooms)
        {
            RoomSubscription? toDispose = null;
            lock (_gate)
            {
                if (_natsOptions.Enabled && _subscriptions.TryGetValue(roomId, out var existing))
                {
                    existing.Connections.TryRemove(connectionId, out _);
                    if (existing.Connections.IsEmpty)
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
    /// bookkeeping as hub connections, so <see cref="Revoke"/> can find and close it too.
    /// </summary>
    public async Task<(ChannelReader<SseOutboundEvent> Reader, CancellationToken Revoked)> RegisterSseConnectionAsync(
        Guid roomId, string connectionId, string userId, int bufferSize)
    {
        var connection = new SseConnection(
            Channel.CreateBounded<SseOutboundEvent>(new BoundedChannelOptions(bufferSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }),
            new CancellationTokenSource());
        var perRoom = _sseChannels.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, SseConnection>());
        perRoom[connectionId] = connection;
        await SubscribeAsync(roomId, connectionId, userId);
        return (connection.Channel.Reader, connection.Revoked.Token);
    }

    /// <summary>Tears down an SSE connection's registration; safe to call more than once.</summary>
    public void UnregisterSseConnection(Guid roomId, string connectionId)
    {
        if (_sseChannels.TryGetValue(roomId, out var perRoom))
        {
            if (perRoom.TryRemove(connectionId, out var connection))
                connection.Channel.Writer.TryComplete();
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

        foreach (var (connectionId, connection) in perRoom)
        {
            if (!connection.Channel.Writer.TryWrite(evt))
            {
                connection.Channel.Writer.TryComplete();
                perRoom.TryRemove(connectionId, out _);
            }
        }
    }

    private static void PublishToHub(ConcurrentDictionary<string, ConnectionInfo> connections, string method, object payload)
    {
        var message = new SerializedHubMessage(new InvocationMessage(method, [payload]));
        foreach (var (_, info) in connections)
            info.Outbox?.Post(message);
    }

    private HubOutbox? CreateOutbox(string connectionId) =>
        _natsOptions.Enabled && _lifetimeManager.Find(connectionId) is { } connection ? new HubOutbox(connection) : null;

    public bool IsSubscribed(string connectionId, Guid roomId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out var info) && info.Rooms.Contains(roomId);
        }
    }

    /// <summary>
    /// Drops every local connection subscribed to <paramref name="roomId"/> as
    /// <paramref name="userId"/>: sends it <c>MembershipRevoked</c>, removes it from its own
    /// bookkeeping, and queues a presence leave (spec §3.5).
    /// </summary>
    public void Revoke(Guid roomId, string userId)
    {
        List<(string ConnectionId, ConnectionInfo Info)> affected = [];
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(roomId, out var subscription))
            {
                foreach (var (connectionId, info) in subscription.Connections)
                {
                    if (info.UserId == userId)
                        affected.Add((connectionId, info));
                }
            }
        }

        var revoked = new SerializedHubMessage(new InvocationMessage("MembershipRevoked", [new MembershipRevoked(roomId, userId)]));
        foreach (var (connectionId, info) in affected)
        {
            info.Outbox?.Post(revoked);

            if (_sseChannels.TryGetValue(roomId, out var perRoom) && perRoom.TryRemove(connectionId, out var sse))
            {
                sse.Channel.Writer.TryComplete();
                sse.Revoked.Cancel();
            }

            Unsubscribe(roomId, connectionId);
            _presenceCoalescer.QueueLeave(roomId, userId);
        }
    }

    private RoomSubscription CreateSubscription(Guid roomId)
    {
        var connections = new ConcurrentDictionary<string, ConnectionInfo>();
        var cts = new CancellationTokenSource();
        var messageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ephemeralReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageTask = Task.Run(() => SubscribeMessagesLoopAsync(roomId, connections, messageReady, cts.Token));
        var ephemeralTask = Task.Run(() => SubscribeEphemeralLoopAsync(roomId, connections, ephemeralReady, cts.Token));
        return new RoomSubscription(connections, cts, Task.WhenAll(messageReady.Task, ephemeralReady.Task), messageTask, ephemeralTask);
    }

    private async Task SubscribeMessagesLoopAsync(
        Guid roomId, ConcurrentDictionary<string, ConnectionInfo> connections, TaskCompletionSource ready, CancellationToken ct)
    {
        // Live fanout rides NATS core, not JetStream (spec §3.1/§3.2): the send path
        // republishes each acked message here with its stream seq attached.
        var subject = $"{_natsOptions.RoomSubjectPrefix}{roomId}{_natsOptions.RoomLiveSubjectSuffix}";
        try
        {
            await using var sub = await _nats.SubscribeCoreAsync<byte[]>(subject, cancellationToken: ct);
            await _nats.PingAsync(ct);
            ready.TrySetResult();

            await foreach (var msg in sub.Msgs.ReadAllAsync(ct))
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

                    PublishToHub(connections, "ReceiveMessage", delivery);

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
            ready.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            _logger.LogError("Message subscription loop error for room {RoomId}: {Error}", roomId, ex.Message);
        }
    }

    private async Task SubscribeEphemeralLoopAsync(
        Guid roomId, ConcurrentDictionary<string, ConnectionInfo> connections, TaskCompletionSource ready, CancellationToken ct)
    {
        var subject = $"room.{roomId}.ephemeral.>";
        try
        {
            await using var sub = await _nats.SubscribeCoreAsync<byte[]>(subject, cancellationToken: ct);
            await _nats.PingAsync(ct);
            ready.TrySetResult();

            await foreach (var msg in sub.Msgs.ReadAllAsync(ct))
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
                            PublishToHub(connections, "UserTyping", typing);
                            PublishToSse(roomId, new SseOutboundEvent("typing", msg.Data, null));
                        }
                    }
                    else if (msg.Subject.EndsWith(".presence", StringComparison.Ordinal))
                    {
                        var presence = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.PresenceDelta);
                        if (presence is not null)
                        {
                            PublishToHub(connections, "PresenceUpdated", presence);
                            PublishToSse(roomId, new SseOutboundEvent("presence", msg.Data, null));
                        }
                    }
                    else if (msg.Subject.EndsWith(".retracted", StringComparison.Ordinal))
                    {
                        var retracted = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.MessageRetracted);
                        if (retracted is not null)
                        {
                            PublishToHub(connections, "MessageRetracted", retracted);
                            PublishToSse(roomId, new SseOutboundEvent("retracted", msg.Data, null));
                        }
                    }
                    else if (msg.Subject.EndsWith(".revoked", StringComparison.Ordinal))
                    {
                        var revoked = JsonSerializer.Deserialize(msg.Data, DotwireJsonContext.Default.MembershipRevoked);
                        if (revoked is not null)
                        {
                            Revoke(revoked.RoomId, revoked.UserId);
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
            ready.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
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
