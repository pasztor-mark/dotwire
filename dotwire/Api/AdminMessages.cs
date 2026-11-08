using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;

namespace Dotwire.Api;

public static partial class Admin
{
    private const int JetStreamNoMessageFound = 10037;
    private const int JetStreamSequenceNotFound = 10043;

    private static void MapMessageEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/rooms/{roomId}/messages", async (
            Guid roomId,
            InjectMessageRequest request,
            HttpContext http,
            MessageCipher cipher,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            INatsJSContext js,
            INatsConnection nats,
            AuditPublisher auditPublisher,
            PresendWebhookClient presendClient,
            PostSendWebhookService postSendService,
            IOptions<DotwireOptions> dotwireOptions,
            IOptions<NatsOptions> natsOptions,
            IOptions<WebhooksOptions> webhooksOptions,
            ILoggerFactory loggerFactory) =>
        {
            var logger = AdminLogger(loggerFactory);
            var maxBytes = dotwireOptions.Value.MaxContentBytes;

            if (Messages.ValidateContent(request.Content, maxBytes) is { } invalid)
                return invalid;

            var senderId = string.IsNullOrEmpty(request.SenderId) ? "system" : request.SenderId;
            if (!IdValidation.IsValid(senderId))
                return ApiResults.InvalidUserId();

            var actorId = http.User.FindFirstValue("sub")!;
            var content = request.Content!;
            var time = DateTimeOffset.UtcNow;

            if (webhooksOptions.Value.Presend.IncludeAdminSends)
            {
                var verdict = await presendClient.EvaluateAsync(roomId, senderId, content, time, "admin", http.RequestAborted);
                var (presendError, rewritten) = Messages.ApplyPresend(verdict, content, maxBytes);
                if (presendError is not null)
                    return presendError;
                content = rewritten;
            }

            var plaintext = Encoding.UTF8.GetBytes(content);

            RoomLiveMessage published;
            try
            {
                published = await MessagePublisher.PublishDurableAsync(
                    roomId, senderId, plaintext, cipher, js, natsOptions.Value, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("JetStream publish failed for injected message in room {RoomId}: {Error}", roomId, ex.Message);
                return ApiResults.StreamUnavailable();
            }

            var audit = new AuditEvent("message.injected", roomId, published.Seq, actorId, senderId, null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError("Audit publish failed for injection in room {RoomId} seq {Seq}: {Error}", roomId, published.Seq, ex.Message);
                await EraseUnauditedInjectionAsync(writeDataSource, js, natsOptions.Value, logger, roomId, published.Seq);
                return ApiResults.AuditUnavailable();
            }

            await MessagePublisher.PublishLiveAsync(published, nats, natsOptions.Value, logger, http.RequestAborted);

            if (webhooksOptions.Value.PostSend.IncludeAdminSends)
                postSendService.Enqueue(new PostSendWebhookRequest(roomId, published.Seq, senderId, content, published.Time, "admin"));

            return Results.Accepted(value: new SendMessageResponse(published.Seq, published.Time));
        });

        group.MapDelete("/rooms/{roomId}/messages/{seq}", async (
            Guid roomId,
            ulong seq,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            INatsJSContext js,
            INatsConnection nats,
            AuditPublisher auditPublisher,
            IOptions<NatsOptions> natsOptions,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (seq == 0 || seq > long.MaxValue)
                return Results.BadRequest();

            var ct = http.RequestAborted;
            var actorId = http.User.FindFirstValue("sub")!;
            var logger = AdminLogger(loggerFactory);
            var options = natsOptions.Value;

            INatsJSStream? stream = null;
            if (options.Enabled)
            {
                try
                {
                    stream = await js.GetStreamAsync(options.RoomsStream, cancellationToken: ct);
                    var stored = await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, ct);
                    if (stored.Message.Subject != $"{options.RoomSubjectPrefix}{roomId}")
                        return Results.NotFound();
                }
                catch (NatsJSApiException ex) when (ex.Error.ErrCode == JetStreamNoMessageFound)
                {
                    await stream!.RefreshAsync(ct);
                    if (seq > stream.Info.State.LastSeq)
                        return Results.NotFound();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("JetStream lookup failed for room {RoomId} seq {Seq}: {Error}", roomId, seq, ex.Message);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }

            var audit = new AuditEvent("message.redacted", roomId, seq, actorId, SubjectId: null, Value: null, DateTimeOffset.UtcNow);
            if (!await TryAuditAsync(auditPublisher, audit, logger, ct))
                return ApiResults.AuditUnavailable();

            int removedRows;
            await using (var cmd = writeDataSource.CreateCommand("SELECT redact_message($1, $2)"))
            {
                cmd.Parameters.AddWithValue(roomId);
                cmd.Parameters.AddWithValue((long)seq);
                removedRows = (int)(await cmd.ExecuteScalarAsync(ct))!;
            }

            if (stream is null)
                return Results.Ok(new RedactMessageResponse(roomId, seq, removedRows > 0, false));

            var removedFromStream = false;
            try
            {
                await stream.DeleteMessageAsync(new StreamMsgDeleteRequest { Seq = seq, NoErase = false }, ct);
                removedFromStream = true;
            }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == JetStreamSequenceNotFound)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("JetStream delete failed for room {RoomId} seq {Seq}: {Error}", roomId, seq, ex.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var retractPayload = JsonSerializer.SerializeToUtf8Bytes(new MessageRetracted(roomId, seq), DotwireJsonContext.Default.MessageRetracted);
            try
            {
                await nats.PublishAsync<byte[]>($"room.{roomId}.ephemeral.retracted", retractPayload, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Retraction broadcast failed for room {RoomId} seq {Seq}: {Error}", roomId, seq, ex.Message);
            }

            return Results.Ok(new RedactMessageResponse(roomId, seq, removedRows > 0, removedFromStream));
        });
    }

    private static async Task EraseUnauditedInjectionAsync(
        NpgsqlDataSource writeDataSource, INatsJSContext js, NatsOptions options, ILogger logger, Guid roomId, ulong seq)
    {
        try
        {
            await using var cmd = writeDataSource.CreateCommand("SELECT redact_message($1, $2)");
            cmd.Parameters.AddWithValue(roomId);
            cmd.Parameters.AddWithValue((long)seq);
            await cmd.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogCritical("Could not tombstone unaudited injection in room {RoomId} seq {Seq}: {Error}", roomId, seq, ex.Message);
        }

        try
        {
            var stream = await js.GetStreamAsync(options.RoomsStream, cancellationToken: CancellationToken.None);
            await stream.DeleteMessageAsync(new StreamMsgDeleteRequest { Seq = seq, NoErase = false }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogCritical("Could not erase unaudited injection from JetStream in room {RoomId} seq {Seq}: {Error}", roomId, seq, ex.Message);
        }
    }
}
