using System.Security.Cryptography;
using System.Text;
using Dotwire.Configuration;
using Microsoft.Extensions.Options;

namespace Dotwire.Crypto;

public sealed class MessageCipher
{
    public const byte EnvelopeV1 = 0x01;

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int SaltSize = 32;
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("dotwire/message/v1");

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly bool _allowLegacyEnvelopes;

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
                    $"Encryption key '{kid}' is not valid base64.");
            }

            if (key.Length != KeySize)
                throw new InvalidOperationException(
                    $"Encryption key '{kid}' must be 32 bytes (got {key.Length}).");

            _keys[kid] = key;
        }

        if (string.IsNullOrEmpty(value.ActiveKeyId) || !_keys.ContainsKey(value.ActiveKeyId))
            throw new InvalidOperationException(
                "Dotwire:Encryption:ActiveKeyId must name a key present in Dotwire:Encryption:Keys.");

        ActiveKeyId = value.ActiveKeyId;
        _allowLegacyEnvelopes = value.AllowLegacyEnvelopes;
    }

    public string ActiveKeyId { get; }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, Guid roomId, string senderId)
    {
        var envelope = new byte[1 + SaltSize + plaintext.Length + TagSize];
        envelope[0] = EnvelopeV1;
        var salt = envelope.AsSpan(1, SaltSize);
        RandomNumberGenerator.Fill(salt);

        Span<byte> derived = stackalloc byte[KeySize + NonceSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _keys[ActiveKeyId], derived, salt, HkdfInfo);

        using var aes = new AesGcm(derived[..KeySize], TagSize);
        aes.Encrypt(
            derived[KeySize..],
            plaintext,
            envelope.AsSpan(1 + SaltSize, plaintext.Length),
            envelope.AsSpan(1 + SaltSize + plaintext.Length, TagSize),
            AssociatedData(roomId, senderId, ActiveKeyId));
        CryptographicOperations.ZeroMemory(derived);

        return envelope;
    }

    public byte[] Decrypt(string keyId, ReadOnlySpan<byte> envelope, Guid roomId, string senderId)
    {
        if (!_keys.TryGetValue(keyId, out var key))
            throw new InvalidOperationException($"Unknown encryption key id '{keyId}'.");

        if (envelope.Length >= 1 + SaltSize + TagSize && envelope[0] == EnvelopeV1)
        {
            try
            {
                return DecryptV1(key, keyId, envelope, roomId, senderId);
            }
            catch (AuthenticationTagMismatchException) when (_allowLegacyEnvelopes)
            {
            }
        }

        if (!_allowLegacyEnvelopes)
            throw new CryptographicException("Envelope is not a valid v1 envelope.");

        return DecryptLegacy(key, envelope);
    }

    private static byte[] DecryptV1(byte[] key, string keyId, ReadOnlySpan<byte> envelope, Guid roomId, string senderId)
    {
        var salt = envelope.Slice(1, SaltSize);
        var ciphertext = envelope[(1 + SaltSize)..^TagSize];

        Span<byte> derived = stackalloc byte[KeySize + NonceSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, derived, salt, HkdfInfo);

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(derived[..KeySize], TagSize);
            aes.Decrypt(derived[KeySize..], ciphertext, envelope[^TagSize..], plaintext, AssociatedData(roomId, senderId, keyId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }

        return plaintext;
    }

    private static byte[] DecryptLegacy(byte[] key, ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < NonceSize + TagSize)
            throw new CryptographicException("Envelope shorter than nonce + tag.");

        var plaintext = new byte[envelope.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            envelope[..NonceSize],
            envelope[NonceSize..^TagSize],
            envelope[^TagSize..],
            plaintext);

        return plaintext;
    }

    private static byte[] AssociatedData(Guid roomId, string senderId, string keyId) =>
        Encoding.UTF8.GetBytes($"{roomId:D}\n{keyId}\n{senderId}");
}
