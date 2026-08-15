using System.Security.Cryptography;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Crypto;

/// <summary>
/// AES-256-GCM message-content encryption (ARCHITECTURE.md, "Encryption at rest").
/// Envelope layout: nonce (12) ‖ ciphertext ‖ tag (16); the key id travels beside the
/// envelope (queryable rotation metadata), never inside it.
/// Never log key material, plaintext, or ciphertext not even in exception paths.
/// </summary>
public sealed class MessageCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public MessageCipher(IOptions<EncryptionOptions> options)
    {
        var value = options.Value;
        foreach (var (kid, base64) in value.Keys)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"Encryption key '{kid}' is not valid base64."); // id only . never the value
            }

            if (key.Length != 32)
                throw new InvalidOperationException(
                    $"Encryption key '{kid}' must be 32 bytes (got {key.Length}).");

            _keys[kid] = key;
        }

        if (string.IsNullOrEmpty(value.ActiveKeyId) || !_keys.ContainsKey(value.ActiveKeyId))
            throw new InvalidOperationException(
                "Dotwire:Encryption:ActiveKeyId must name a key present in Dotwire:Encryption:Keys.");

        ActiveKeyId = value.ActiveKeyId;
    }

    /// <summary>Key id new messages are encrypted under.</summary>
    public string ActiveKeyId { get; }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var envelope = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = envelope.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_keys[ActiveKeyId], TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            envelope.AsSpan(NonceSize, plaintext.Length),
            envelope.AsSpan(NonceSize + plaintext.Length, TagSize));

        return envelope;
    }

    public byte[] Decrypt(string keyId, ReadOnlySpan<byte> envelope)
    {
        if (!_keys.TryGetValue(keyId, out var key))
            throw new InvalidOperationException($"Unknown encryption key id '{keyId}'.");

        if (envelope.Length < NonceSize + TagSize)
            throw new InvalidOperationException("Envelope shorter than nonce + tag.");

        var plaintext = new byte[envelope.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            envelope[..NonceSize],
            envelope[NonceSize..^TagSize],
            envelope[^TagSize..],
            plaintext);

        return plaintext;
    }
}
