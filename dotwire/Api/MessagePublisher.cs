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
    public sealed record PublishResult(ulong Seq, DateTimeOffset Time);

    public static async Task<PublishResult> PublishAsync(
        Guid roomId,
        string senderId,
        byte[] plaintext,
        MessageCipher cipher,
        INatsJSContext js,
        INatsConnection nats,
        NatsOptions natsOptions,
        ILogger logger,
        CancellationToken ct)
    {
        var time = DateTimeOffset.UtcNow;
        var keyId = cipher.ActiveKeyId;
        var encrypted = cipher.Encrypt(plaintext);
        var message = new RoomMessage(roomId, senderId, time, keyId, encrypted);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, DotwireJsonContext.Default.RoomMessage);

        var ack = await js.PublishAsync<byte[]>(
            $"{natsOptions.RoomSubjectPrefix}{roomId}",
            payload,
            cancellationToken: ct);
        if (ack.Error is not null)
            throw new InvalidOperationException($"JetStream publish error {ack.Error.Code}");

        try
        {
            var live = new RoomLiveMessage(roomId, ack.Seq, senderId, time, keyId, encrypted);
            var livePayload = JsonSerializer.SerializeToUtf8Bytes(live, DotwireJsonContext.Default.RoomLiveMessage);
            await nats.PublishAsync(
                $"{natsOptions.RoomSubjectPrefix}{roomId}{natsOptions.RoomLiveSubjectSuffix}",
                livePayload,
                cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Live republish failed for room {RoomId} seq {Seq}: {Error}", roomId, ack.Seq, ex.Message);
        }

        return new PublishResult(ack.Seq, time);
    }
}
