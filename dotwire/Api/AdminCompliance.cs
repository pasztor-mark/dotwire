using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dotwire.Auth;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Npgsql;

namespace Dotwire.Api;

public static partial class Admin
{
    private const int DsarFlushThresholdBytes = 16 * 1024;

    private static void MapComplianceEndpoints(RouteGroupBuilder group)
    {
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
            if (!await TryAuditAsync(auditPublisher, audit, AdminLogger(loggerFactory), http.RequestAborted))
                return ApiResults.AuditUnavailable();

            await tx.CommitAsync(CancellationToken.None);

            return Results.Ok(new RetentionResponse(request.MessagesDays));
        });

        group.MapGet("/users/{userId}/export", async (
            string userId,
            [FromKeyedServices(PostgresDataSources.Read)] NpgsqlDataSource readDataSource,
            IAccessStore accessStore,
            MessageCipher cipher,
            AuditPublisher auditPublisher,
            ILoggerFactory loggerFactory,
            HttpContext http) =>
        {
            if (!IdValidation.IsValid(userId))
                return ApiResults.InvalidUserId();

            var ct = http.RequestAborted;
            var actorId = http.User.FindFirstValue("sub")!;
            var logger = AdminLogger(loggerFactory);

            var audit = new AuditEvent("dsar.exported", null, null, actorId, userId, null, DateTimeOffset.UtcNow);
            if (!await TryAuditAsync(auditPublisher, audit, logger, ct))
                return ApiResults.AuditUnavailable();

            var role = await accessStore.GetRoleAsync(userId, ct);

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
                        var decrypted = cipher.Decrypt(keyId, content, roomId, userId);
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
}
