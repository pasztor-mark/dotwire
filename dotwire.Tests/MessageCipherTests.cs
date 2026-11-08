using System.Security.Cryptography;
using System.Text;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class MessageCipherTests
{
    private static readonly Guid RoomA = Guid.NewGuid();
    private static readonly Guid RoomB = Guid.NewGuid();

    private static MessageCipher Create(string activeKeyId = "k1", bool allowLegacy = true, params (string Kid, byte[] Key)[] extra)
    {
        var options = new EncryptionOptions { ActiveKeyId = activeKeyId, AllowLegacyEnvelopes = allowLegacy };
        options.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        foreach (var (kid, key) in extra)
            options.Keys[kid] = Convert.ToBase64String(key);
        return new MessageCipher(Options.Create(options));
    }

    private static byte[] LegacyEnvelope(byte[] key, byte[] plaintext)
    {
        var envelope = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(envelope.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(envelope.AsSpan(0, 12), plaintext, envelope.AsSpan(12, plaintext.Length), envelope.AsSpan(12 + plaintext.Length, 16));
        return envelope;
    }

    [Fact]
    public void RoundTripsUnderActiveKey()
    {
        var cipher = Create();
        var plaintext = Encoding.UTF8.GetBytes("hello dotwire");

        var envelope = cipher.Encrypt(plaintext, RoomA, "alice");
        var decrypted = cipher.Decrypt(cipher.ActiveKeyId, envelope, RoomA, "alice");

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(MessageCipher.EnvelopeV1, envelope[0]);
        Assert.Equal(1 + 32 + plaintext.Length + 16, envelope.Length);
        Assert.NotEqual(plaintext, envelope[33..^16]);
    }

    [Fact]
    public void SaltsAreUniquePerMessage()
    {
        var cipher = Create();
        var plaintext = "same message"u8.ToArray();

        var a = cipher.Encrypt(plaintext, RoomA, "alice");
        var b = cipher.Encrypt(plaintext, RoomA, "alice");

        Assert.NotEqual(a[1..33], b[1..33]);
        Assert.NotEqual(a[33..], b[33..]);
    }

    [Fact]
    public void TamperedEnvelopeThrows()
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray(), RoomA, "alice");
        envelope[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(cipher.ActiveKeyId, envelope, RoomA, "alice"));
    }

    [Fact]
    public void CiphertextMovedToAnotherRoomDoesNotDecrypt()
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray(), RoomA, "alice");

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(cipher.ActiveKeyId, envelope, RoomB, "alice"));
    }

    [Fact]
    public void CiphertextAttributedToAnotherSenderDoesNotDecrypt()
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray(), RoomA, "alice");

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(cipher.ActiveKeyId, envelope, RoomA, "mallory"));
    }

    [Fact]
    public void LegacyEnvelopeStillDecryptsWhenAllowed()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = Create("old", allowLegacy: true, ("old", key));
        var legacy = LegacyEnvelope(key, "history"u8.ToArray());

        Assert.Equal("history"u8.ToArray(), cipher.Decrypt("old", legacy, RoomA, "alice"));
    }

    [Fact]
    public void LegacyEnvelopeIsRejectedWhenLegacyDisabled()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = Create("old", allowLegacy: false, ("old", key));
        var legacy = LegacyEnvelope(key, "history"u8.ToArray());
        legacy[0] = 0x00;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt("old", legacy, RoomA, "alice"));
    }

    [Fact]
    public void LegacyEnvelopeStartingWithVersionByteFallsBack()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = Create("old", allowLegacy: true, ("old", key));
        byte[] legacy;
        do
        {
            legacy = LegacyEnvelope(key, new byte[40]);
        } while (legacy[0] != MessageCipher.EnvelopeV1);

        Assert.Equal(new byte[40], cipher.Decrypt("old", legacy, RoomA, "alice"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(27)]
    [InlineData(48)]
    public void TruncatedEnvelopeThrowsCryptographicException(int length)
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray(), RoomA, "alice")[..length];

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(cipher.ActiveKeyId, envelope, RoomA, "alice"));
    }

    [Fact]
    public void UnknownKeyIdThrows()
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray(), RoomA, "alice");

        Assert.Throws<InvalidOperationException>(() => cipher.Decrypt("nope", envelope, RoomA, "alice"));
    }

    [Fact]
    public void KeyIdIsBoundToTheEnvelope()
    {
        var shared = RandomNumberGenerator.GetBytes(32);
        var cipher = Create("a", allowLegacy: false, ("a", shared), ("b", shared));
        var envelope = cipher.Encrypt("payload"u8.ToArray(), RoomA, "alice");

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt("b", envelope, RoomA, "alice"));
    }

    [Fact]
    public void DecryptsOldKeyAfterRotation()
    {
        var oldKey = RandomNumberGenerator.GetBytes(32);
        var oldCipher = Create("old", true, ("old", oldKey));
        var envelope = oldCipher.Encrypt("history"u8.ToArray(), RoomA, "alice");

        var rotated = Create("k1", true, ("old", oldKey));
        Assert.Equal("history"u8.ToArray(), rotated.Decrypt("old", envelope, RoomA, "alice"));
    }

    [Theory]
    [InlineData("", "k1")]
    [InlineData("missing", "k1")]
    public void InvalidActiveKeyConfigThrows(string activeKeyId, string presentKid)
    {
        var options = new EncryptionOptions { ActiveKeyId = activeKeyId };
        options.Keys[presentKid] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.Throws<InvalidOperationException>(() => new MessageCipher(Options.Create(options)));
    }

    [Fact]
    public void WrongSizeKeyThrows()
    {
        var options = new EncryptionOptions { ActiveKeyId = "k1" };
        options.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        Assert.Throws<InvalidOperationException>(() => new MessageCipher(Options.Create(options)));
    }
}
