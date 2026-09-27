using System.Security.Cryptography;
using System.Text;
using Dotwire.Api;
using Dotwire.Nats;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Fixed vectors for the audit hash chain (spec §3.6). These pin the exact byte layout so
/// a future refactor can't silently change what the chain commits to.
/// </summary>
public class AuditCanonicalFormTests
{
    private static readonly Guid RoomId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Compute_ProducesExactSevenFieldLayout()
    {
        var time = new DateTimeOffset(2026, 9, 7, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567);
        var evt = new AuditEvent("member.added", RoomId, MessageSeq: null, ActorId: "admin-1", SubjectId: "user-2", Value: null, time);

        var canonical = AuditCanonicalForm.Compute(evt);

        var expected = "member.added\n" +
                       "11111111-2222-3333-4444-555555555555\n" +
                       "\n" +
                       "admin-1\n" +
                       "user-2\n" +
                       "\n" +
                       "2026-09-07T12:34:56.123456Z";
        Assert.Equal(expected, Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void Compute_EmptyOptionalFieldsAreEmptyStrings()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var evt = new AuditEvent("audit.read", RoomId: null, MessageSeq: null, ActorId: "auditor-1", SubjectId: null, Value: null, time);

        var canonical = AuditCanonicalForm.Compute(evt);

        Assert.Equal("audit.read\n\n\nauditor-1\n\n\n2026-01-01T00:00:00.000000Z", Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void Compute_MessageSeqAndValueAreDecimal()
    {
        var time = DateTimeOffset.UnixEpoch;
        var evt = new AuditEvent("message.injected", RoomId, MessageSeq: 42UL, ActorId: "admin", SubjectId: "system", Value: 30, time);

        var canonical = AuditCanonicalForm.Compute(evt);

        Assert.Equal(
            "message.injected\n11111111-2222-3333-4444-555555555555\n42\nadmin\nsystem\n30\n1970-01-01T00:00:00.000000Z",
            Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void Hash_MatchesIndependentlyComputedSha256OfPrevHashConcatCanonical()
    {
        var evt = new AuditEvent("role.set.member", RoomId: null, MessageSeq: null, ActorId: "admin-1", SubjectId: "user-1", Value: null,
            new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var prevHash = AuditCanonicalForm.GenesisHash();

        var canonical = AuditCanonicalForm.Compute(evt);
        var actual = AuditCanonicalForm.Hash(prevHash, canonical);

        // Recomputed independently via the raw primitive, concatenating bytes ourselves,
        // rather than calling back into Hash()'s own buffer-building logic.
        var buffer = new byte[prevHash.Length + canonical.Length];
        Buffer.BlockCopy(prevHash, 0, buffer, 0, prevHash.Length);
        Buffer.BlockCopy(canonical, 0, buffer, prevHash.Length, canonical.Length);
        var expected = SHA256.HashData(buffer);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hash_DifferentPrevHashProducesDifferentHash()
    {
        var evt = new AuditEvent("role.set.member", null, null, "admin-1", "user-1", null, DateTimeOffset.UtcNow);
        var canonical = AuditCanonicalForm.Compute(evt);

        var genesisHash = AuditCanonicalForm.Hash(AuditCanonicalForm.GenesisHash(), canonical);
        var otherPrev = SHA256.HashData(Encoding.UTF8.GetBytes("not genesis"));
        var otherHash = AuditCanonicalForm.Hash(otherPrev, canonical);

        Assert.NotEqual(genesisHash, otherHash);
    }

    [Fact]
    public void TruncateToMicroseconds_DropsSubMicrosecondTicks()
    {
        var time = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero).AddTicks(19); // 1.9 microseconds
        var truncated = AuditCanonicalForm.TruncateToMicroseconds(time);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero).AddTicks(10), truncated);
    }

    [Fact]
    public void HashChain_TamperingWithARowInvalidatesItsRecomputedHash()
    {
        // Builds a short in-memory chain, then tampers with the middle row's stored fields
        // and asserts recomputing the hash for that row no longer matches what was stored -
        // this is the exact check the /audit/verify endpoint (a later task) will reuse.
        var events = new[]
        {
            new AuditEvent("role.set.member", null, null, "admin-1", "user-1", null, new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)),
            new AuditEvent("member.added", RoomId, null, "admin-1", "user-1", null, new DateTimeOffset(2026, 9, 7, 0, 0, 1, TimeSpan.Zero)),
            new AuditEvent("message.injected", RoomId, 1UL, "admin-1", "system", null, new DateTimeOffset(2026, 9, 7, 0, 0, 2, TimeSpan.Zero)),
        };

        var prevHash = AuditCanonicalForm.GenesisHash();
        var stored = new List<(AuditEvent Event, byte[] PrevHash, byte[] Hash)>();
        foreach (var evt in events)
        {
            var hash = AuditCanonicalForm.Hash(prevHash, AuditCanonicalForm.Compute(evt));
            stored.Add((evt, prevHash, hash));
            prevHash = hash;
        }

        // Untampered chain recomputes byte-for-byte.
        foreach (var row in stored)
        {
            var recomputed = AuditCanonicalForm.Hash(row.PrevHash, AuditCanonicalForm.Compute(row.Event));
            Assert.Equal(row.Hash, recomputed);
        }

        // Tamper with the middle row's actor id (content itself is never in the chain, but
        // any stored field is), keeping its stored hash unchanged.
        var tamperedEvent = stored[1].Event with { ActorId = "attacker" };

        var recomputedTampered = AuditCanonicalForm.Hash(stored[1].PrevHash, AuditCanonicalForm.Compute(tamperedEvent));
        Assert.NotEqual(stored[1].Hash, recomputedTampered);

        // And the next row's prev_hash link no longer matches the tampered row's (recomputed) hash.
        Assert.NotEqual(recomputedTampered, stored[2].PrevHash);
    }
}
