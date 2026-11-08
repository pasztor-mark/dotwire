using System.Text.Json;
using Dotwire.Configuration;
using Dotwire.Crypto;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Dotwire.Api;

/// <summary>
/// Shared send-path plumbing (spec §3.2 steps 3-4) used by both the member send endpoint
/// and admin message injection (§3.3): encrypt, publish to the room's JetStream subject,
/// await the ack, then best-effort republish a seq-tagged live message on NATS core.
/// </summary>
public static class MessagePublisher
{
    public sealed record PublishResult(ulong Seq, DateTimeOffset Time, bool Duplicate = false);

    public static async Task<PublishResult> PublishAsync(
        Guid roomId,
        string senderId,
        byte[] plaintext,
        MessageCipher cipher,
        INatsJSContext js,
        INatsConnection nats,
        NatsOptions natsOptions,
        ILogger logger,
        CancellationToken ct,
        string? msgId = null)
    {
        var (live, duplicate) = await PublishDurableCoreAsync(roomId, senderId, plaintext, cipher, js, natsOptions, msgId, ct);
        if (!duplicate)
            await PublishLiveAsync(live, nats, natsOptions, logger, ct);
        return new PublishResult(live.Seq, live.Time, duplicate);
    }

    public static async Task<RoomLiveMessage> PublishDurableAsync(
        Guid roomId,
        string senderId,
        byte[] plaintext,
        MessageCipher cipher,
        INatsJSContext js,
        NatsOptions natsOptions,
        CancellationToken ct) =>
        (await PublishDurableCoreAsync(roomId, senderId, plaintext, cipher, js, natsOptions, null, ct)).Live;

    private static async Task<(RoomLiveMessage Live, bool Duplicate)> PublishDurableCoreAsync(
        Guid roomId,
        string senderId,
        byte[] plaintext,
        MessageCipher cipher,
        INatsJSContext js,
        NatsOptions natsOptions,
        string? msgId,
        CancellationToken ct)
    {
        var time = DateTimeOffset.UtcNow;
        var keyId = cipher.ActiveKeyId;
        var encrypted = cipher.Encrypt(plaintext, roomId, senderId);
        var message = new RoomMessage(roomId, senderId, time, keyId, encrypted);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, DotwireJsonContext.Default.RoomMessage);

        var ack = await js.PublishAsync<byte[]>(
            $"{natsOptions.RoomSubjectPrefix}{roomId}",
            payload,
            opts: msgId is null ? null : new NatsJSPubOpts { MsgId = msgId },
            cancellationToken: ct);
        if (ack.Error is not null)
            throw new InvalidOperationException($"JetStream publish error {ack.Error.Code}");

        return (new RoomLiveMessage(roomId, ack.Seq, senderId, time, keyId, encrypted), ack.Duplicate);
    }

    public static async Task PublishLiveAsync(
        RoomLiveMessage live,
        INatsConnection nats,
        NatsOptions natsOptions,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var livePayload = JsonSerializer.SerializeToUtf8Bytes(live, DotwireJsonContext.Default.RoomLiveMessage);
            await nats.PublishAsync(
                $"{natsOptions.RoomSubjectPrefix}{live.RoomId}{natsOptions.RoomLiveSubjectSuffix}",
                livePayload,
                cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Live republish failed for room {RoomId} seq {Seq}: {Error}", live.RoomId, live.Seq, ex.Message);
        }
    }
}
