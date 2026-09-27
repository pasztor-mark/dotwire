using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dotwire.Api;

namespace Dotwire.Nats;

/// <summary>
/// The audit hash-chain's canonical byte form and hash step (spec §3.6). Ids can never
/// contain a newline (<see cref="Dotwire.Auth.IdValidation"/>), so the seven `\n`-joined
/// fields are unambiguous. Every consumer - the writer and the verify endpoint - must
/// compute the exact same bytes from the exact same stored columns, so this is the one
/// place either recomputes from.
/// </summary>
public static class AuditCanonicalForm
{
    public const int HashSize = 32;

    /// <summary>UTC, truncated to whole microseconds - the precision Postgres `timestamptz` keeps.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        var ticks = utc.Ticks - utc.Ticks % 10; // 1 tick = 100ns; 10 ticks = 1 microsecond.
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>UTF-8 of the seven canonical fields joined by `\n`, no trailing newline.</summary>
    public static byte[] Compute(AuditEvent evt)
    {
        var time = TruncateToMicroseconds(evt.Time);
        var fields = new[]
        {
            evt.EventType,
            evt.RoomId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty,
            evt.MessageSeq?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            evt.ActorId ?? string.Empty,
            evt.SubjectId ?? string.Empty,
            evt.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            time.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "Z",
        };
        return Encoding.UTF8.GetBytes(string.Join('\n', fields));
    }

    /// <summary>`SHA256(prevHash ‖ canonical)`. <paramref name="prevHash"/> must be exactly 32 raw bytes.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> prevHash, ReadOnlySpan<byte> canonical)
    {
        if (prevHash.Length != HashSize)
            throw new ArgumentException($"prevHash must be {HashSize} bytes.", nameof(prevHash));

        var buffer = new byte[prevHash.Length + canonical.Length];
        prevHash.CopyTo(buffer);
        canonical.CopyTo(buffer.AsSpan(prevHash.Length));
        return SHA256.HashData(buffer);
    }

    /// <summary>Genesis previous-hash: 32 zero bytes.</summary>
    public static byte[] GenesisHash() => new byte[HashSize];
}
