using System.Security.Cryptography;
using System.Text;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class MessageCipherTests
{
    private static MessageCipher Create(string activeKeyId = "k1", params (string Kid, byte[] Key)[] extra)
    {
        var options = new EncryptionOptions { ActiveKeyId = activeKeyId };
        options.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        foreach (var (kid, key) in extra)
            options.Keys[kid] = Convert.ToBase64String(key);
        return new MessageCipher(Options.Create(options));
    }

    [Fact]
    public void RoundTripsUnderActiveKey()
    {
        var cipher = Create();
        var plaintext = Encoding.UTF8.GetBytes("hello dotwire");

        var envelope = cipher.Encrypt(plaintext);
        var decrypted = cipher.Decrypt(cipher.ActiveKeyId, envelope);

        Assert.Equal(plaintext, decrypted);
        // nonce(12) + ciphertext + tag(16)
        Assert.Equal(12 + plaintext.Length + 16, envelope.Length);
        Assert.NotEqual(plaintext, envelope[12..^16]); // actually encrypted
    }

    [Fact]
    public void NoncesAreUniquePerMessage()
    {
        var cipher = Create();
        var plaintext = "same message"u8.ToArray();

        var a = cipher.Encrypt(plaintext);
        var b = cipher.Encrypt(plaintext);

        Assert.NotEqual(a[..12], b[..12]);
    }

    [Fact]
    public void TamperedEnvelopeThrows()
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray());
        envelope[^1] ^= 0xFF; // flip a tag bit

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(cipher.ActiveKeyId, envelope));
    }

    [Fact]
    public void UnknownKeyIdThrows()
    {
        var cipher = Create();
        var envelope = cipher.Encrypt("payload"u8.ToArray());

        Assert.Throws<InvalidOperationException>(() => cipher.Decrypt("nope", envelope));
    }

    [Fact]
    public void DecryptsOldKeyAfterRotation()
    {
        // Rotation = new active id; old ids stay decryptable (ARCHITECTURE.md).
        var oldKey = RandomNumberGenerator.GetBytes(32);
        var oldCipher = Create("old", ("old", oldKey));
        var envelope = oldCipher.Encrypt("history"u8.ToArray());

        var rotated = Create("k1", ("old", oldKey)); // active back to k1, old retained
        Assert.Equal("history"u8.ToArray(), rotated.Decrypt("old", envelope));
    }

    [Theory]
    [InlineData("", "k1")]           // no active id
    [InlineData("missing", "k1")]    // active id not in Keys
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
        options.Keys["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); // not 32

        Assert.Throws<InvalidOperationException>(() => new MessageCipher(Options.Create(options)));
    }
}
