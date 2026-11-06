using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;

namespace Dotwire.Api;

public sealed record SetUserRoleRequest(string Role);

public sealed record UserRoleResponse(string UserId, string Role);

public sealed record AddMembersRequest(string[] UserIds, bool? EnsureMemberRole = null);

public sealed record RoomMembersResponse(Guid RoomId, string[] Members);

public sealed record UserRoomsResponse(string UserId, Guid[] RoomIds);

public static class Admin
{
    private const int DsarFlushThresholdBytes = 16 * 1024;

    public static void MapAdminEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization()
            .AddEndpointFilter<RequireAdminFilter>()
            .RequireRateLimiting("admin");

        group.MapPut("/users/{userId}/role", async (
            string userId,
            SetUserRoleRequest request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            Dotwire.Nats.AuditPublisher auditPublisher,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            if (request.Role is not ("member" or "auditor" or "admin"))
                return Results.BadRequest();

            await using var conn = await writeDataSource.OpenConnectionAsync(http.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO user_roles (user_id, role) VALUES ($1, $2) ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role", conn, tx))
            {
                cmd.Parameters.AddWithValue(userId);
                cmd.Parameters.AddWithValue(request.Role);
                await cmd.ExecuteNonQueryAsync(http.RequestAborted);
            }

            var actorId = http.User.FindFirstValue("sub")!;
            var audit = new AuditEvent($"role.set.{request.Role}", null, null, actorId, userId, null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("Dotwire.Api.Admin")
                    .LogError("Audit publish failed for role set of {UserId}: {Error}", userId, ex.Message);
                return ApiResults.AuditUnavailable();
            }

            await tx.CommitAsync(CancellationToken.None);

            return Results.Ok(new UserRoleResponse(userId, request.Role));
        });

        group.MapGet("/users/{userId}/role", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            await using var cmd = readDataSource.CreateCommand(
                "SELECT role FROM user_roles WHERE user_id = $1");
            cmd.Parameters.AddWithValue(userId);
            var role = (string?)await cmd.ExecuteScalarAsync(http.RequestAborted);

            if (role is null)
                return Results.NotFound();

            return Results.Ok(new UserRoleResponse(userId, role));
        });

        group.MapDelete("/users/{userId}/role", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            Dotwire.Nats.AuditPublisher auditPublisher,
            INatsConnection nats,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var logger = loggerFactory.CreateLogger("Dotwire.Api.Admin");

            await using var conn = await writeDataSource.OpenConnectionAsync(http.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);

            var roomIds = new List<Guid>();
            await using (var delMembers = new NpgsqlCommand("DELETE FROM room_members WHERE user_id = $1 RETURNING room_id", conn, tx))
            {
                delMembers.Parameters.AddWithValue(userId);
                await using var reader = await delMembers.ExecuteReaderAsync(http.RequestAborted);
                while (await reader.ReadAsync(http.RequestAborted))
                    roomIds.Add(reader.GetGuid(0));
            }

            await using (var delRoles = new NpgsqlCommand("DELETE FROM user_roles WHERE user_id = $1", conn, tx))
            {
                delRoles.Parameters.AddWithValue(userId);
                await delRoles.ExecuteNonQueryAsync(http.RequestAborted);
            }

            var actorId = http.User.FindFirstValue("sub")!;
            var audit = new AuditEvent("role.deleted", null, null, actorId, userId, null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Audit publish failed for role deletion of {UserId}: {Error}", userId, ex.Message);
                return ApiResults.AuditUnavailable();
            }

            await tx.CommitAsync(CancellationToken.None);

            foreach (var roomId in roomIds)
            {
                await PublishRevocationAsync(nats, logger, roomId, userId);
            }

            return Results.NoContent();
        });

        group.MapPost("/rooms/{roomId}/members", async (
            string roomId,
            AddMembersRequest request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            Dotwire.Nats.AuditPublisher auditPublisher,
            HttpContext http) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid))
                return Results.BadRequest();

            if (request.UserIds is null || request.UserIds.Length == 0)
                return Results.BadRequest();

            foreach (var userId in request.UserIds)
            {
                if (!IdValidation.IsValid(userId))
                    return ApiResults.InvalidUserId();
            }

            // Omitted/false leaves today's behavior unchanged: membership only, no role grant.
            var ensureMemberRole = request.EnsureMemberRole ?? false;

            await using (var conn = await writeDataSource.OpenConnectionAsync(http.RequestAborted))
            await using (var tx = await conn.BeginTransactionAsync(http.RequestAborted))
            {
                foreach (var userId in request.UserIds)
                {
                    await using (var cmd = new NpgsqlCommand(
                        "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn, tx))
                    {
                        cmd.Parameters.AddWithValue(roomGuid);
                        cmd.Parameters.AddWithValue(userId);
                        await cmd.ExecuteNonQueryAsync(http.RequestAborted);
                    }

                    if (ensureMemberRole)
                    {
                        // Never overwrites an existing auditor/admin row (spec §3.4).
                        await using var roleCmd = new NpgsqlCommand(
                            "INSERT INTO user_roles (user_id, role) VALUES ($1, 'member') ON CONFLICT DO NOTHING", conn, tx);
                        roleCmd.Parameters.AddWithValue(userId);
                        await roleCmd.ExecuteNonQueryAsync(http.RequestAborted);
                    }
                }

                var actorId = http.User.FindFirstValue("sub")!;
                foreach (var userId in request.UserIds)
                {
                    var audit = new AuditEvent("member.added", roomGuid, null, actorId, userId, null, DateTimeOffset.UtcNow);
                    try
                    {
                        await auditPublisher.PublishAsync(audit, http.RequestAborted);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return ApiResults.AuditUnavailable();
                    }
                }

                await tx.CommitAsync(CancellationToken.None);
            }

            return Results.Ok(new RoomMembersResponse(roomGuid, request.UserIds));
        });

        group.MapDelete("/rooms/{roomId}/members/{userId}", async (
            string roomId,
            string userId,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            Dotwire.Nats.AuditPublisher auditPublisher,
            INatsConnection nats,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid))
                return Results.BadRequest();
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var logger = loggerFactory.CreateLogger("Dotwire.Api.Admin");

            await using var conn = await writeDataSource.OpenConnectionAsync(http.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
            await using (var cmd = new NpgsqlCommand(
                "DELETE FROM room_members WHERE room_id = $1 AND user_id = $2", conn, tx))
            {
                cmd.Parameters.AddWithValue(roomGuid);
                cmd.Parameters.AddWithValue(userId);
                await cmd.ExecuteNonQueryAsync(http.RequestAborted);
            }

            var actorId = http.User.FindFirstValue("sub")!;
            var audit = new AuditEvent("member.removed", roomGuid, null, actorId, userId, null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Audit publish failed for member removal room {RoomId} user {UserId}: {Error}", roomGuid, userId, ex.Message);
                return ApiResults.AuditUnavailable();
            }

            await tx.CommitAsync(CancellationToken.None);

            await PublishRevocationAsync(nats, logger, roomGuid, userId);

            return Results.NoContent();
        });

        group.MapGet("/rooms/{roomId}/members", async (
            string roomId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid))
                return Results.BadRequest();

            var members = new List<string>();
            await using var cmd = readDataSource.CreateCommand(
                "SELECT user_id FROM room_members WHERE room_id = $1 ORDER BY user_id ASC");
            cmd.Parameters.AddWithValue(roomGuid);
            await using var reader = await cmd.ExecuteReaderAsync(http.RequestAborted);
            while (await reader.ReadAsync(http.RequestAborted))
            {
                members.Add(reader.GetString(0));
            }

            return Results.Ok(new RoomMembersResponse(roomGuid, members.ToArray()));
        });

        group.MapGet("/users/{userId}/rooms", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var roomIds = new List<Guid>();
            await using var cmd = readDataSource.CreateCommand(
                "SELECT room_id FROM room_members WHERE user_id = $1");
            cmd.Parameters.AddWithValue(userId);
            await using var reader = await cmd.ExecuteReaderAsync(http.RequestAborted);
            while (await reader.ReadAsync(http.RequestAborted))
            {
                roomIds.Add(reader.GetGuid(0));
            }

            return Results.Ok(new UserRoomsResponse(userId, roomIds.ToArray()));
        });

        // Admin message injection (spec §3.3). No membership check: the injected sender id
        // on the wire is whatever the admin asserts, and the injection is audited.
        group.MapPost("/rooms/{roomId}/messages", async (
            string roomId,
            InjectMessageRequest request,
            HttpContext http,
            MessageCipher cipher,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            INatsJSContext js,
            INatsConnection nats,
            Dotwire.Nats.AuditPublisher auditPublisher,
            Dotwire.Nats.PresendWebhookClient presendClient,
            Dotwire.Nats.PostSendWebhookService postSendService,
            IOptions<DotwireOptions> dotwireOptions,
            IOptions<NatsOptions> natsOptions,
            IOptions<WebhooksOptions> webhooksOptions,
            ILoggerFactory loggerFactory) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid))
                return Results.BadRequest();

            var logger = loggerFactory.CreateLogger("Dotwire.Api.Admin");
            var maxBytes = dotwireOptions.Value.MaxContentBytes;

            if (string.IsNullOrEmpty(request.Content))
                return ApiResults.InvalidContent();
            if (Encoding.UTF8.GetByteCount(request.Content) > maxBytes)
                return ApiResults.InvalidContent();

            var senderId = string.IsNullOrEmpty(request.SenderId) ? "system" : request.SenderId;
            if (!IdValidation.IsValid(senderId))
                return ApiResults.InvalidUserId();

            var actorId = http.User.FindFirstValue("sub")!;
            var content = request.Content;
            var time = DateTimeOffset.UtcNow;

            // Presend/postsend webhooks only run for injections when explicitly opted into
            // (both IncludeAdminSends default false) - injected content is host-authored (spec §3.3).
            if (webhooksOptions.Value.Presend.IncludeAdminSends)
            {
                var verdict = await presendClient.EvaluateAsync(roomGuid, senderId, content, time, "admin", http.RequestAborted);
                switch (verdict.Outcome)
                {
                    case Dotwire.Nats.PresendOutcome.Rejected:
                        return ApiResults.PresendRejected(verdict.Reason);
                    case Dotwire.Nats.PresendOutcome.Unavailable:
                        return ApiResults.PresendUnavailable();
                    case Dotwire.Nats.PresendOutcome.AllowedWithRewrite:
                        if (Encoding.UTF8.GetByteCount(verdict.Content!) > maxBytes)
                            return ApiResults.InvalidContent();
                        content = verdict.Content!;
                        break;
                }
            }

            var plaintext = Encoding.UTF8.GetBytes(content);

            RoomLiveMessage published;
            try
            {
                published = await MessagePublisher.PublishDurableAsync(
                    roomGuid, senderId, plaintext, cipher, js, natsOptions.Value, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("JetStream publish failed for injected message in room {RoomId}: {Error}", roomGuid, ex.Message);
                return ApiResults.StreamUnavailable();
            }

            var audit = new AuditEvent("message.injected", roomGuid, published.Seq, actorId, senderId, null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError("Audit publish failed for injection in room {RoomId} seq {Seq}: {Error}", roomGuid, published.Seq, ex.Message);
                await EraseUnauditedInjectionAsync(writeDataSource, js, natsOptions.Value, logger, roomGuid, published.Seq);
                return ApiResults.AuditUnavailable();
            }

            await MessagePublisher.PublishLiveAsync(published, nats, natsOptions.Value, logger, http.RequestAborted);

            if (webhooksOptions.Value.PostSend.IncludeAdminSends)
                postSendService.Enqueue(new PostSendWebhookRequest(roomGuid, published.Seq, senderId, content, published.Time, "admin"));

            return Results.Accepted(value: new SendMessageResponse(published.Seq, published.Time));
        });

        // Redact/delete message (ARCHITECTURE.md, "Redaction mechanics"; GDPR Art. 17).
        // Physical erasure in both stores, ids-only audit event, ephemeral retraction to
        // live subscribers. Every step is idempotent, so an operator can simply retry a
        // response that wasn't 200.
        group.MapDelete("/rooms/{roomId}/messages/{seq}", async (
            string roomId,
            ulong seq,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            INatsJSContext js,
            INatsConnection nats,
            Dotwire.Nats.AuditPublisher auditPublisher,
            IOptions<NatsOptions> natsOptions,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!Guid.TryParse(roomId, out var roomGuid) || seq == 0)
                return Results.BadRequest();

            var ct = http.RequestAborted;
            var actorId = http.User.FindFirstValue("sub")!;
            var logger = loggerFactory.CreateLogger("Dotwire.Api.Admin");
            var options = natsOptions.Value;

            INatsJSStream? stream = null;
            if (options.Enabled)
            {
                try
                {
                    stream = await js.GetStreamAsync(options.RoomsStream, cancellationToken: ct);
                    var stored = await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, ct);
                    if (stored.Message.Subject != $"{options.RoomSubjectPrefix}{roomGuid}")
                        return Results.NotFound();
                }
                catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10037)
                {
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("JetStream lookup failed for room {RoomId} seq {Seq}: {Error}", roomGuid, seq, ex.Message);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }

            // Audit: ids only, never content. Durable (JetStream) so the audit-writer
            // consumer can chain it.
            var audit = new AuditEvent("message.redacted", roomGuid, seq, actorId, SubjectId: null, Value: null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Audit publish failed for redaction of room {RoomId} seq {Seq}: {Error}", roomGuid, seq, ex.Message);
                return ApiResults.AuditUnavailable();
            }

            // 1. Postgres: tombstone + delete via the SECURITY DEFINER path (0006_redaction.sql).
            //    The tombstone is what makes this safe against the batch-writer lag.
            int removedRows;
            await using (var cmd = writeDataSource.CreateCommand("SELECT redact_message($1, $2)"))
            {
                cmd.Parameters.AddWithValue(roomGuid);
                cmd.Parameters.AddWithValue((long)seq);
                removedRows = (int)(await cmd.ExecuteScalarAsync(ct))!;
            }

            if (stream is null)
                return Results.Ok(new RedactMessageResponse(roomGuid, seq, removedRows > 0, false));

            // 2. JetStream: per-message secure erase by stream sequence. 404 means it is
            //    already gone (aged out past MaxAge, or redacted earlier) - still a success.
            var removedFromStream = false;
            try
            {
                await stream.DeleteMessageAsync(new StreamMsgDeleteRequest { Seq = seq, NoErase = false }, ct);
                removedFromStream = true;
            }
            // "Already gone" comes back as a JetStream API-level error (ErrCode 10043,
            // JSSequenceNotFoundErr - aged out past MaxAge, or redacted earlier), not an
            // HTTP 404 - the API's own Code for this case is 400.
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10043)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("JetStream delete failed for room {RoomId} seq {Seq}: {Error}", roomGuid, seq, ex.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // 3. Retraction to live subscribers rides NATS core (ephemeral traffic rule):
            //    never persisted, never on an ack path. The erasure above is the durable
            //    fact; a client that misses this simply never sees the row on gap-fill.
            var retracted = new MessageRetracted(roomGuid, seq);
            var retractPayload = JsonSerializer.SerializeToUtf8Bytes(retracted, DotwireJsonContext.Default.MessageRetracted);
            try
            {
                await nats.PublishAsync<byte[]>($"room.{roomGuid}.ephemeral.retracted", retractPayload, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Retraction broadcast failed for room {RoomId} seq {Seq}: {Error}", roomGuid, seq, ex.Message);
            }

            return Results.Ok(new RedactMessageResponse(roomGuid, seq, removedRows > 0, removedFromStream));
        });

        // Retention (spec §3.11). The migrating node also applies Dotwire:Retention:MessagesDays
        // at startup (Program.cs); whichever set last wins - manage retention in one place.
        group.MapGet("/retention", async (
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            HttpContext http) =>
        {
            await using var cmd = readDataSource.CreateCommand("SELECT get_message_retention()");
            var days = (int)(await cmd.ExecuteScalarAsync(http.RequestAborted))!;
            return Results.Ok(new RetentionResponse(days));
        });

        group.MapPut("/retention", async (
            SetRetentionRequest request,
            [FromKeyedServices(PostgresDataSources.Write)] NpgsqlDataSource writeDataSource,
            AuditPublisher auditPublisher,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (request.MessagesDays < 0 || request.MessagesDays > 36500)
                return ApiResults.InvalidRequest("messagesDays must be between 0 and 36500");

            await using var conn = await writeDataSource.OpenConnectionAsync(http.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
            await using (var cmd = new NpgsqlCommand("SELECT set_message_retention($1)", conn, tx))
            {
                cmd.Parameters.AddWithValue(request.MessagesDays);
                await cmd.ExecuteScalarAsync(http.RequestAborted);
            }

            var actorId = http.User.FindFirstValue("sub")!;
            var audit = new AuditEvent("retention.set", null, null, actorId, null, request.MessagesDays, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, http.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("Dotwire.Api.Admin")
                    .LogError("Audit publish failed for retention.set: {Error}", ex.Message);
                return ApiResults.AuditUnavailable();
            }

            await tx.CommitAsync(CancellationToken.None);

            return Results.Ok(new RetentionResponse(request.MessagesDays));
        });

        // DSAR export (spec §3.12, GDPR Art. 15/20). Streamed row-by-row so an arbitrarily
        // large user history never gets buffered in memory.
        group.MapGet("/users/{userId}/export", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            MessageCipher cipher,
            AuditPublisher auditPublisher,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var ct = http.RequestAborted;
            var actorId = http.User.FindFirstValue("sub")!;
            var logger = loggerFactory.CreateLogger("Dotwire.Api.Admin");

            // Audit first: a stream write can't cleanly turn into an error response once started.
            var audit = new AuditEvent("dsar.exported", null, null, actorId, userId, null, DateTimeOffset.UtcNow);
            try
            {
                await auditPublisher.PublishAsync(audit, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Audit publish failed for DSAR export of {UserId}: {Error}", userId, ex.Message);
                return ApiResults.AuditUnavailable();
            }

            string? role;
            await using (var roleCmd = readDataSource.CreateCommand("SELECT role FROM user_roles WHERE user_id = $1"))
            {
                roleCmd.Parameters.AddWithValue(userId);
                role = (string?)await roleCmd.ExecuteScalarAsync(ct);
            }

            var roomIds = new List<Guid>();
            await using (var roomsCmd = readDataSource.CreateCommand("SELECT room_id FROM room_members WHERE user_id = $1"))
            {
                roomsCmd.Parameters.AddWithValue(userId);
                await using var reader = await roomsCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    roomIds.Add(reader.GetGuid(0));
            }

            http.Response.ContentType = "application/json";
            await using var writer = new Utf8JsonWriter(http.Response.Body);

            writer.WriteStartObject();
            writer.WriteString("userId", userId);
            writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
            if (role is null)
                writer.WriteNull("role");
            else
                writer.WriteString("role", role);

            writer.WriteStartArray("roomIds");
            foreach (var roomId in roomIds)
                writer.WriteStringValue(roomId);
            writer.WriteEndArray();

            writer.WriteStartArray("messages");
            await using (var msgCmd = readDataSource.CreateCommand(
                "SELECT room_id, seq, time, key_id, content FROM messages WHERE sender_id = $1 ORDER BY time, seq"))
            {
                msgCmd.Parameters.AddWithValue(userId);
                await using var reader = await msgCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var roomId = reader.GetGuid(0);
                    var seq = (ulong)reader.GetInt64(1);
                    var time = reader.GetFieldValue<DateTimeOffset>(2);
                    var keyId = reader.GetString(3);
                    var content = (byte[])reader.GetValue(4);

                    writer.WriteStartObject();
                    writer.WriteString("roomId", roomId);
                    writer.WriteNumber("seq", seq);
                    writer.WriteString("time", time);
                    try
                    {
                        var decrypted = cipher.Decrypt(keyId, content);
                        writer.WriteString("content", Encoding.UTF8.GetString(decrypted));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError("Failed to decrypt message {Seq} in room {RoomId} during DSAR export: {Error}", seq, roomId, ex.Message);
                        writer.WriteNull("content");
                        writer.WriteBoolean("undecryptable", true);
                    }
                    writer.WriteEndObject();
                    if (writer.BytesPending > DsarFlushThresholdBytes)
                        await writer.FlushAsync(ct);
                }
            }
            writer.WriteEndArray();

            writer.WriteStartArray("auditEvents");
            await using (var auditCmd = readDataSource.CreateCommand(
                "SELECT id, event_type, room_id, message_seq, actor_id, subject_id, value, event_time " +
                "FROM audit_log WHERE actor_id = $1 OR subject_id = $1 ORDER BY id"))
            {
                auditCmd.Parameters.AddWithValue(userId);
                await using var reader = await auditCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", reader.GetInt64(0));
                    writer.WriteString("eventType", reader.GetString(1));
                    if (reader.IsDBNull(2)) writer.WriteNull("roomId"); else writer.WriteString("roomId", reader.GetGuid(2));
                    if (reader.IsDBNull(3)) writer.WriteNull("messageSeq"); else writer.WriteNumber("messageSeq", (ulong)reader.GetInt64(3));
                    if (reader.IsDBNull(4)) writer.WriteNull("actorId"); else writer.WriteString("actorId", reader.GetString(4));
                    if (reader.IsDBNull(5)) writer.WriteNull("subjectId"); else writer.WriteString("subjectId", reader.GetString(5));
                    if (reader.IsDBNull(6)) writer.WriteNull("value"); else writer.WriteNumber("value", reader.GetInt64(6));
                    writer.WriteString("time", reader.GetFieldValue<DateTimeOffset>(7));
                    writer.WriteEndObject();
                    if (writer.BytesPending > DsarFlushThresholdBytes)
                        await writer.FlushAsync(ct);
                }
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync(ct);
            return Results.Empty;
        });
    }

    // Best-effort ephemeral broadcast so whichever gateway node holds the live connection
    // can revoke it locally (spec §3.5); never fails the request.
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

    private static async Task PublishRevocationAsync(
        INatsConnection nats, ILogger logger, Guid roomId, string userId)
    {
        var revoked = new MembershipRevoked(roomId, userId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(revoked, DotwireJsonContext.Default.MembershipRevoked);
        try
        {
            await nats.PublishAsync<byte[]>($"room.{roomId}.ephemeral.revoked", payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Revocation broadcast failed for room {RoomId} user {UserId}: {Error}", roomId, userId, ex.Message);
        }
    }
}
