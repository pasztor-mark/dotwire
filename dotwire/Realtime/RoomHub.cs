using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Dotwire.Realtime;

[Authorize]
public sealed class RoomHub(
    IAccessStore accessStore,
    RoomInterestManager interestManager,
    PresenceCoalescer presenceCoalescer,
    TypingThrottle typingThrottle,
    UserConnectionLimiter connectionLimiter,
    INatsConnection nats,
    IOptions<NatsOptions> natsOptions) : Hub
{
    private const string ConnectionSlotKey = "dotwire_connection_slot";

    public override Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
            throw new HubException("Unauthorized");
        if (!connectionLimiter.TryAcquire(sub))
            throw new HubException("TooManyConnections");

        Context.Items[ConnectionSlotKey] = sub;
        return base.OnConnectedAsync();
    }

    public async Task Subscribe(Guid roomId)
    {
        var user = Context.User;
        var sub = user?.FindFirstValue("sub");
        var claimedRole = user?.FindFirstValue("dw:role");

        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(claimedRole))
            throw new HubException("Unauthorized");

        var ct = Context.ConnectionAborted;
        if (await accessStore.GetRoleAsync(sub, ct) != claimedRole)
            throw new HubException("Forbidden");

        if (!await accessStore.IsMemberAsync(roomId, sub, ct))
            throw new HubException("Forbidden");

        if (!await interestManager.SubscribeAsync(roomId, Context.ConnectionId, sub, claimedRole))
            return;

        bool stillMember;
        try
        {
            stillMember = await accessStore.IsMemberAsync(roomId, sub, ct);
        }
        catch
        {
            interestManager.Unsubscribe(roomId, Context.ConnectionId);
            throw;
        }

        if (!stillMember)
        {
            interestManager.Unsubscribe(roomId, Context.ConnectionId);
            throw new HubException("Forbidden");
        }

        presenceCoalescer.QueueJoin(roomId, sub);
    }

    public void Unsubscribe(Guid roomId)
    {
        var sub = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
            return;

        if (interestManager.Unsubscribe(roomId, Context.ConnectionId))
            presenceCoalescer.QueueLeave(roomId, sub);
    }

    public async Task Typing(Guid roomId, bool isTyping)
    {
        var sub = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
            return;

        if (!interestManager.IsSubscribed(Context.ConnectionId, roomId))
            return;

        if (!natsOptions.Value.Enabled)
            return;

        if (!typingThrottle.ShouldPublish(sub, roomId, isTyping, Stopwatch.GetTimestamp()))
            return;

        var notification = new TypingNotification(roomId, sub, isTyping);
        var payload = JsonSerializer.SerializeToUtf8Bytes(notification, DotwireJsonContext.Default.TypingNotification);

        await nats.PublishAsync<byte[]>($"room.{roomId}.ephemeral.typing", payload, cancellationToken: Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var sub = Context.User?.FindFirstValue("sub");
        if (!string.IsNullOrEmpty(sub))
        {
            var rooms = interestManager.DisconnectAll(Context.ConnectionId);
            foreach (var roomId in rooms)
            {
                presenceCoalescer.QueueLeave(roomId, sub);
            }
        }

        if (Context.Items.Remove(ConnectionSlotKey, out var slot) && slot is string slotUser)
            connectionLimiter.Release(slotUser);

        await base.OnDisconnectedAsync(exception);
    }
}
