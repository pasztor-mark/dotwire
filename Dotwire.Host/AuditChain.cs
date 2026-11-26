using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dotwire.Host;

public static class AuditChain
{
    public const int HashSize = 32;

    public static byte[] Compute(AuditEntry entry)
    {
        var utc = entry.Time.ToUniversalTime();
        var time = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        var fields = new[]
        {
            entry.EventType,
            entry.RoomId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty,
            entry.MessageSeq?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            entry.ActorId ?? string.Empty,
            entry.SubjectId ?? string.Empty,
            entry.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            time.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "Z",
        };
        return Encoding.UTF8.GetBytes(string.Join('\n', fields));
    }

    public static byte[] Hash(ReadOnlySpan<byte> prevHash, ReadOnlySpan<byte> canonical)
    {
        if (prevHash.Length != HashSize)
            throw new ArgumentException($"prevHash must be {HashSize} bytes.", nameof(prevHash));

        var buffer = new byte[prevHash.Length + canonical.Length];
        prevHash.CopyTo(buffer);
        canonical.CopyTo(buffer.AsSpan(prevHash.Length));
        return SHA256.HashData(buffer);
    }

    public static byte[] GenesisHash() => new byte[HashSize];
}
