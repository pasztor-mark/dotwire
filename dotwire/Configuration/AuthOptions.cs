namespace Dotwire.Configuration;

/// <summary>
/// Host JWT verification (AUTH.md, "Token contract"). dotwire holds PUBLIC keys only .
/// RS256, selected per-token by the required <c>kid</c> header - and mints nothing, ever.
/// Never conflate these with the AES at-rest keys in <see cref="EncryptionOptions"/>.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Expected <c>iss</c> claim - the host identifier.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Expected <c>aud</c> claim.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Inline path: kid → PEM-encoded RSA public key (airgapped/simple deployments).</summary>
    public Dictionary<string, string> Keys { get; set; } = new();

    /// <summary>JWKS path: standard key-set endpoint, polled and cached.</summary>
    public string? JwksUrl { get; set; }

    /// <summary>JWKS cache refresh interval; also refreshed (rate-limited) on unknown kid.</summary>
    public int JwksRefreshMinutes { get; set; } = 5;
}
