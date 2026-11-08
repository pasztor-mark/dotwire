using System.Text.Json;
using Dotwire.Auth;
using Dotwire.Nats;
using NATS.Client.Core;
using Npgsql;

namespace Dotwire.Api;

public sealed record SetUserRoleRequest(string Role);

public sealed record UserRoleResponse(string UserId, string Role);

public sealed record AddMembersRequest(string[] UserIds, bool? EnsureMemberRole = null);

public sealed record RoomMembersResponse(Guid RoomId, string[] Members);

public sealed record UserRoomsResponse(string UserId, Guid[] RoomIds);

public static partial class Admin
{
    public const int MaxMembersPerRequest = 500;
    private const long RoleMutationLockKey = 0x726F_6C65_6D75_74;
    private const int RevocationAttempts = 3;
    private static readonly TimeSpan RevocationAttemptTimeout = TimeSpan.FromSeconds(2);

    public static void MapAdminEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization()
            .AddEndpointFilter(new RequireRoleFilter("admin"))
            .RequireRateLimiting("admin");

        MapRoleEndpoints(group);
        MapMemberEndpoints(group);
        MapMessageEndpoints(group);
        MapComplianceEndpoints(group);
    }

    private static ILogger AdminLogger(ILoggerFactory loggerFactory) => loggerFactory.CreateLogger("Dotwire.Api.Admin");

    private static async Task<bool> TryAuditAsync(AuditPublisher auditPublisher, AuditEvent audit, ILogger logger, CancellationToken ct)
    {
        try
        {
            await auditPublisher.PublishAsync(audit, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Audit publish failed for {EventType} (subject {SubjectId}, room {RoomId}): {Error}",
                audit.EventType, audit.SubjectId, audit.RoomId, ex.Message);
            return false;
        }
    }

    private static async Task LockRoleMutationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({RoleMutationLockKey})", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> CurrentRoleForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT role FROM user_roles WHERE user_id = $1 FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue(userId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<bool> HasOtherAdminAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM user_roles WHERE role = 'admin' AND user_id <> $1)", conn, tx);
        cmd.Parameters.AddWithValue(userId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    internal static async Task<bool> PublishWithRetryAsync(
        Func<CancellationToken, ValueTask> publish, int attempts, TimeSpan attemptTimeout, Action<int, string>? onFailure = null)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(attemptTimeout);
                await publish(cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(attempt, ex.Message);
            }

            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
        }

        return false;
    }

    private static async Task PublishRevocationAsync(
        INatsConnection nats, ILogger logger, Guid roomId, string userId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new MembershipRevoked(roomId, userId), DotwireJsonContext.Default.MembershipRevoked);
        var delivered = await PublishWithRetryAsync(
            ct => nats.PublishAsync<byte[]>($"room.{roomId}.ephemeral.revoked", payload, cancellationToken: ct),
            RevocationAttempts,
            RevocationAttemptTimeout,
            (attempt, error) => logger.LogWarning(
                "Revocation broadcast attempt {Attempt} failed for room {RoomId} user {UserId}: {Error}", attempt, roomId, userId, error));

        if (!delivered)
            logger.LogError("Revocation broadcast failed for room {RoomId} user {UserId}; the membership sweep will drop live sockets", roomId, userId);
    }
}
