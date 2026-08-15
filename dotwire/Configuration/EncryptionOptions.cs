namespace Dotwire.Configuration;

/// <summary>
/// At-rest encryption keys (AES-256-GCM, encrypt-before-JetStream-publish - see
/// ARCHITECTURE.md, "Encryption at rest &amp; data lifecycle"). Keys are key-id-tagged:
/// rotation adds a new id and leaves old ids in place to decrypt old messages.
/// These are dotwire's own symmetric keys. never conflate with the host's JWT signing keys.
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Dotwire:Encryption";

    /// <summary>Key id new messages are encrypted under.</summary>
    public string ActiveKeyId { get; set; } = "";

    /// <summary>Key id → base64-encoded 32-byte key. Values are secrets: never log them.</summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}
