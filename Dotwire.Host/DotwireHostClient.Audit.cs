namespace Dotwire.Host;

public sealed partial class DotwireHostClient
{
    /// <inheritdoc />
    public Task<AuditPage> ReadAuditLogAsync(long afterId = 0, int limit = 100, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"/audit/?afterId={afterId}&limit={limit}", AuditorToken(), DotwireHostJsonContext.Default.AuditPage, ct);

    /// <inheritdoc />
    public Task<AuditVerification> VerifyAuditLogAsync(bool full = false, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"/audit/verify?full={(full ? "true" : "false")}", AuditorToken(), DotwireHostJsonContext.Default.AuditVerification, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditCheckpoint>> GetAuditCheckpointsAsync(int limit = 30, CancellationToken ct = default)
    {
        var dto = await SendAsync(HttpMethod.Get, $"/audit/checkpoints?limit={limit}", AuditorToken(),
            DotwireHostJsonContext.Default.AuditCheckpointsResponseDto, ct);
        return dto.Checkpoints;
    }

    /// <inheritdoc />
    public async Task<int> GetMessageRetentionDaysAsync(CancellationToken ct = default)
    {
        var dto = await SendAsync(HttpMethod.Get, "/admin/retention", AdminToken(), DotwireHostJsonContext.Default.RetentionDto, ct);
        return dto.MessagesDays;
    }

    /// <inheritdoc />
    public async Task<int> SetMessageRetentionDaysAsync(int days, CancellationToken ct = default)
    {
        var body = JsonBody(new SetRetentionRequestDto(days), DotwireHostJsonContext.Default.SetRetentionRequestDto);
        var dto = await SendAsync(HttpMethod.Put, "/admin/retention", AdminToken(), DotwireHostJsonContext.Default.RetentionDto, ct, body);
        return dto.MessagesDays;
    }
}
