using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Npgsql;

namespace Dotwire.Realtime;

[Authorize]
public sealed class RoomHub(
    [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
    RoomInterestManager interestManager,
    PresenceCoalescer presenceCoalescer,
    INatsConnection nats,
    IOptions<NatsOptions> natsOptions) : Hub
{
    private const string LastTypingTicksKey = "dotwire_last_typing_ticks";
    private static readonly long TypingCooldownTicks = Stopwatch.Frequency / 2;

    public async Task Subscribe(Guid roomId)
    {
        var user = Context.User;
        var sub = user?.FindFirstValue("sub");
        var claimedRole = user?.FindFirstValue("dw:role");

        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(claimedRole))
            throw new HubException("Unauthorized");

        await using (var roleCmd = readDataSource.CreateCommand("SELECT role FROM user_roles WHERE user_id = $1"))
        {
            roleCmd.Parameters.AddWithValue(sub);
            var tableRole = (string?)await roleCmd.ExecuteScalarAsync(Context.ConnectionAborted);
            if (tableRole is null || tableRole != claimedRole)
                throw new HubException("Forbidden");
        }

        await using (var memberCmd = readDataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM room_members WHERE room_id = $1 AND user_id = $2)"))
        {
            memberCmd.Parameters.AddWithValue(roomId);
            memberCmd.Parameters.AddWithValue(sub);
            var isMember = (bool)(await memberCmd.ExecuteScalarAsync(Context.ConnectionAborted))!;
            if (!isMember)
                throw new HubException("Forbidden");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString(), Context.ConnectionAborted);

        if (!interestManager.Subscribe(roomId, Context.ConnectionId, sub))
            return;

        presenceCoalescer.QueueJoin(roomId, sub);
    }

    public async Task Unsubscribe(Guid roomId)
    {
        var sub = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
            return;

        if (interestManager.Unsubscribe(roomId, Context.ConnectionId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString());
            presenceCoalescer.QueueLeave(roomId, sub);
        }
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

        if (isTyping)
        {
            var now = Stopwatch.GetTimestamp();
            var lastTypingMap = GetLastTypingMap();
            lock (lastTypingMap)
            {
                if (lastTypingMap.TryGetValue(roomId, out var last) && now - last < TypingCooldownTicks)
                    return;
                lastTypingMap[roomId] = now;
            }
        }

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

        await base.OnDisconnectedAsync(exception);
    }

    private Dictionary<Guid, long> GetLastTypingMap()
    {
        if (!Context.Items.TryGetValue(LastTypingTicksKey, out var obj) || obj is not Dictionary<Guid, long> map)
        {
            map = [];
            Context.Items[LastTypingTicksKey] = map;
        }
        return map;
    }
}
