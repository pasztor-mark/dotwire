using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Nats;
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
    IOptions<NatsOptions> natsOptions,
    MessageSend messageSend,
    SendRateLimiter sendRateLimiter,
    IOptions<DotwireOptions> dotwireOptions,
    DotwireMetrics metrics,
    AccessLogPublisher accessLog) : Hub
{
    private const string ConnectionSlotKey = "dotwire_connection_slot";

    public override Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
            throw new HubException("Unauthorized");
        if (!connectionLimiter.TryAcquire(sub))
        {
            metrics.Rejection("hub", "too_many_connections", "connect");
            throw new HubException("TooManyConnections");
        }

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
        var (role, isMember) = await accessStore.GetRoleAndMembershipAsync(roomId, sub, ct);
        if (role != claimedRole || !isMember)
            throw new HubException("Forbidden");

        AccessEvent? opened = null;
        if (accessLog.IsEnabled)
        {
            if (interestManager.IsSubscribed(Context.ConnectionId, roomId))
                return;

            opened = AccessEvents.OpenEvent(user!, roomId, AccessEvents.Hub)
                ?? throw new HubException("AccessLogUnavailable");
            try
            {
                await accessLog.RecordAsync(opened, ct);
            }
            catch (AccessLogUnavailableException)
            {
                throw new HubException("AccessLogUnavailable");
            }
        }

        if (!await interestManager.SubscribeAsync(roomId, Context.ConnectionId, sub, claimedRole, opened))
        {
            if (opened is not null)
                accessLog.RecordClose(AccessEvents.Close(opened, AccessEvents.Error, DateTimeOffset.UtcNow));
            return;
        }

        bool stillMember;
        try
        {
            stillMember = await accessStore.IsMemberAsync(roomId, sub, ct);
        }
        catch
        {
            interestManager.Unsubscribe(roomId, Context.ConnectionId, AccessEvents.Error);
            throw;
        }

        if (!stillMember)
        {
            interestManager.Unsubscribe(roomId, Context.ConnectionId, AccessEvents.Revoked);
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

    public async Task<SendMessageResponse> Send(Guid roomId, string content, string? idempotencyKey)
    {
        if (!dotwireOptions.Value.HubSend.Enabled)
            throw new HubException("HubSendDisabled");

        var sub = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
            throw new HubException("Unauthorized");

        if (!sendRateLimiter.TryAcquire(sub, out _))
        {
            metrics.Rejection("hub", "rate_limited", "send");
            throw new HubException("RateLimited");
        }

        if (!interestManager.IsSubscribed(Context.ConnectionId, roomId))
            throw new HubException("Forbidden");

        var (response, rejection) = await messageSend.SendAsync(roomId, sub, content, idempotencyKey, "hub", Context.ConnectionAborted);
        if (rejection is not null)
            throw new HubException(rejection.Reason is null ? rejection.Code : $"{rejection.Code}:{rejection.Reason}");

        return response!;
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
