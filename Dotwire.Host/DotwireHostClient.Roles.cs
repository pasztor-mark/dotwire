using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    /// <inheritdoc />
    public async Task<UserRole> SetUserRoleAsync(string userId, string role, CancellationToken ct = default)
    {
        var body = JsonBody(new SetUserRoleRequestDto(role), DotwireHostJsonContext.Default.SetUserRoleRequestDto);
        var dto = await SendAsync(HttpMethod.Put, $"/admin/users/{Uri.EscapeDataString(userId)}/role", AdminToken(),
            DotwireHostJsonContext.Default.UserRole, ct, body);
        return dto;
    }

    /// <inheritdoc />
    public async Task<UserRole?> GetUserRoleAsync(string userId, CancellationToken ct = default)
    {
        var path = $"/admin/users/{Uri.EscapeDataString(userId)}/role";
        try
        {
            using var response = await SendRawAsync(HttpMethod.Get, path, AdminToken(), null, ct);
            return await response.Content.ReadFromJsonAsync(DotwireHostJsonContext.Default.UserRole, ct);
        }
        catch (DotwireApiException ex) when (ex.Kind == DotwireErrorKind.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task DeleteUserRoleAsync(string userId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/admin/users/{Uri.EscapeDataString(userId)}/role", AdminToken(), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetUserRoomsAsync(string userId, CancellationToken ct = default)
    {
        var dto = await SendAsync(HttpMethod.Get, $"/admin/users/{Uri.EscapeDataString(userId)}/rooms", AdminToken(),
            DotwireHostJsonContext.Default.UserRooms, ct);
        return dto.RoomIds;
    }

    /// <inheritdoc />
    public async Task<UserExport> ExportUserAsync(string userId, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"/admin/users/{Uri.EscapeDataString(userId)}/export", AdminToken(), null, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ParseExportAsync(stream, ct);
    }

    /// <inheritdoc />
    public async Task ExportUserAsync(string userId, Stream destination, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"/admin/users/{Uri.EscapeDataString(userId)}/export", AdminToken(), null, ct);
        await response.Content.CopyToAsync(destination, ct);
    }

    /// <summary>
    /// Parses the DSAR export's hand-rolled JSON (spec §3.12; written field-by-field via
    /// Utf8JsonWriter on the server, not through a shared DTO) into <see cref="UserExport"/>.
    /// </summary>
    private static async Task<UserExport> ParseExportAsync(Stream stream, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var userId = root.GetProperty("userId").GetString()!;
        var exportedAt = root.GetProperty("exportedAt").GetDateTimeOffset();
        var role = root.TryGetProperty("role", out var roleEl) && roleEl.ValueKind != JsonValueKind.Null
            ? roleEl.GetString()
            : null;

        var roomIds = root.GetProperty("roomIds").EnumerateArray().Select(e => e.GetGuid()).ToArray();

        var messages = root.GetProperty("messages").EnumerateArray().Select(m => new UserExportMessage(
            m.GetProperty("roomId").GetGuid(),
            (ulong)m.GetProperty("seq").GetInt64(),
            m.GetProperty("time").GetDateTimeOffset(),
            m.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null,
            m.TryGetProperty("undecryptable", out var u) && u.ValueKind == JsonValueKind.True)).ToArray();

        var auditEvents = root.GetProperty("auditEvents").EnumerateArray().Select(a => new UserExportAuditEvent(
            a.GetProperty("id").GetInt64(),
            a.GetProperty("eventType").GetString()!,
            a.TryGetProperty("roomId", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetGuid() : null,
            a.TryGetProperty("messageSeq", out var ms) && ms.ValueKind != JsonValueKind.Null ? (ulong)ms.GetInt64() : null,
            a.TryGetProperty("actorId", out var ai) && ai.ValueKind != JsonValueKind.Null ? ai.GetString() : null,
            a.TryGetProperty("subjectId", out var si) && si.ValueKind != JsonValueKind.Null ? si.GetString() : null,
            a.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt64() : null,
            a.GetProperty("time").GetDateTimeOffset())).ToArray();

        return new UserExport(userId, exportedAt, role, roomIds, messages, auditEvents);
    }
}
