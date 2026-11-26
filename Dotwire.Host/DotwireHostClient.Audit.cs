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

    public async Task<AuditArchiveResult> ArchiveAuditLogAsync(Stream destination, long afterId = 0, string? previousHash = null, CancellationToken ct = default)
    {
        if (afterId > 0 && previousHash is null)
            throw new ArgumentException("previousHash is required when afterId is greater than zero.", nameof(previousHash));

        var prev = previousHash is null ? AuditChain.GenesisHash() : Convert.FromHexString(previousHash);
        var cursor = afterId;
        long count = 0;
        while (true)
        {
            var page = await ReadAuditLogAsync(cursor, 500, ct);
            foreach (var entry in page.Events)
            {
                var hash = AuditChain.Hash(prev, AuditChain.Compute(entry));
                if (Convert.ToHexString(hash).ToLowerInvariant() != entry.Hash)
                    throw new InvalidDataException($"Audit chain broken at id {entry.Id}: stored hash does not match SHA256(previous hash || canonical form).");
                await WriteLineAsync(destination, entry, DotwireHostJsonContext.Default.AuditEntry, ct);
                prev = hash;
                cursor = entry.Id;
                count++;
            }
            if (!page.HasMore || page.Events.Count == 0)
                break;
        }

        foreach (var checkpoint in await GetAuditCheckpointsAsync(365, ct))
            await WriteLineAsync(destination, new AuditCheckpointLineDto(checkpoint), DotwireHostJsonContext.Default.AuditCheckpointLineDto, ct);

        return new AuditArchiveResult(count, cursor, count == 0 ? previousHash : Convert.ToHexString(prev).ToLowerInvariant());
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

    public Task<ComplianceSettings> GetComplianceSettingsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "/admin/compliance", AdminToken(), DotwireHostJsonContext.Default.ComplianceSettings, ct);

    public Task<LegalHold> PlaceRoomLegalHoldAsync(Guid roomId, string matterRef, CancellationToken ct = default) =>
        PlaceLegalHoldAsync(new PlaceLegalHoldRequestDto(roomId, null, matterRef), ct);

    public Task<LegalHold> PlaceUserLegalHoldAsync(string userId, string matterRef, CancellationToken ct = default) =>
        PlaceLegalHoldAsync(new PlaceLegalHoldRequestDto(null, userId, matterRef), ct);

    private Task<LegalHold> PlaceLegalHoldAsync(PlaceLegalHoldRequestDto request, CancellationToken ct)
    {
        var body = JsonBody(request, DotwireHostJsonContext.Default.PlaceLegalHoldRequestDto);
        return SendAsync(HttpMethod.Post, "/admin/holds", AdminToken(), DotwireHostJsonContext.Default.LegalHold, ct, body);
    }

    public Task ReleaseLegalHoldAsync(long holdId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/admin/holds/{holdId}", AdminToken(), ct);

    public async Task<IReadOnlyList<LegalHold>> ListLegalHoldsAsync(bool activeOnly = true, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, $"/admin/holds?active={(activeOnly ? "true" : "false")}", AdminToken(),
            DotwireHostJsonContext.Default.LegalHoldArray, ct);
}
