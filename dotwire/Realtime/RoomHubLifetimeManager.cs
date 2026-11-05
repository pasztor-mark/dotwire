using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Dotwire.Realtime;

public sealed class RoomHubLifetimeManager(ILogger<DefaultHubLifetimeManager<RoomHub>> logger)
    : DefaultHubLifetimeManager<RoomHub>(logger)
{
    private readonly ConcurrentDictionary<string, HubConnectionContext> _connections = new();

    public HubConnectionContext? Find(string connectionId) => _connections.GetValueOrDefault(connectionId);

    public override Task OnConnectedAsync(HubConnectionContext connection)
    {
        _connections[connection.ConnectionId] = connection;
        return base.OnConnectedAsync(connection);
    }

    public override Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
        return base.OnDisconnectedAsync(connection);
    }
}
