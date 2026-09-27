namespace Dotwire.Host;

public class DotwireHostOptions
{
    /// <summary>Preseeded by migration 0004; the same default the TypeScript host SDK uses.</summary>
    public const string DefaultAdminUserId = "host-admin";

    /// <summary>Preseeded by migration 0004; the same default the TypeScript host SDK uses.</summary>
    public const string DefaultAuditorUserId = "host-auditor";

    public required Uri BaseUrl { get; init; }

    public required string Issuer { get; init; }
    public string Audience { get; init; } = "dotwire";
    public required string KeyId { get; init; }
    public required string PrivateKeyPem { get; init; }

    /// <summary>
    /// The <c>sub</c> the client mints admin tokens for (redaction, role/membership admin, DSAR,
    /// retention). Must hold the <c>admin</c> role in <c>user_roles</c> - the table is
    /// authoritative, not the token.
    /// </summary>
    public string AdminUserId { get; init; } = DefaultAdminUserId;

    /// <summary>
    /// The <c>sub</c> the client mints auditor tokens for (audit read/verify/checkpoints).
    /// Must hold the <c>auditor</c> role in <c>user_roles</c>.
    /// </summary>
    public string AuditorUserId { get; init; } = DefaultAuditorUserId;

    /// <summary>Default TTL for tokens this client mints internally (as opposed to <see cref="DotwireHostClient.MintToken"/>'s explicit ttl).</summary>
    public TimeSpan DefaultTokenTtl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-request HTTP timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When set, <see cref="DotwireHostClient.HandlePresendWebhookAsync"/> and
    /// <see cref="DotwireHostClient.HandlePostSendWebhookAsync"/> verify the
    /// <c>X-Dotwire-Signature</c> header (HMAC-SHA256 of the raw body) against this secret,
    /// matching the server's <c>Dotwire:Webhooks:Secret</c>.
    /// </summary>
    public string? WebhookSecret { get; init; }

    /// <summary>How long a resolved (user -> role) lookup is cached before <see cref="DotwireHostClient"/> re-resolves it.</summary>
    public TimeSpan RoleCacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
