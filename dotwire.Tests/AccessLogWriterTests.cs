using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dotwire.Api;
using Dotwire.Nats;
using Xunit;

namespace dotwire.Tests;

public class AccessLogWriterTests
{
    private static byte[] Bytes(AccessEvent evt) => JsonSerializer.SerializeToUtf8Bytes(evt, DotwireJsonContext.Default.AccessEvent);

    private static AccessEvent Read() => new(
        AccessEvents.HistoryRead, DateTimeOffset.UtcNow, Guid.NewGuid(), "u1", null, "member", AccessEvents.Rest, Count: 0);

    [Fact]
    public void ParseOrNull_ValidHistoryRead_ReturnsRecord()
    {
        var parsed = AccessLogWriterService.ParseOrNull(Bytes(Read()));
        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.Count);
        Assert.Null(parsed.FromSeq);
        Assert.Null(parsed.ToSeq);
    }

    [Fact]
    public void ParseOrNull_UnknownEvent_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Event = "nope" })));

    [Fact]
    public void ParseOrNull_InvalidUserId_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { UserId = "a\u0000b" })));

    [Fact]
    public void ParseOrNull_OpenedWithoutWindowId_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with
        {
            Event = AccessEvents.Opened, Via = AccessEvents.Hub, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        })));

    [Fact]
    public void ParseOrNull_ClosedWithoutReason_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Event = AccessEvents.Closed, WindowId = Guid.NewGuid() })));

    [Fact]
    public void ParseOrNull_UnknownReasonViaOrRole_ReturnsNull()
    {
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Reason = "x" })));
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Via = "x" })));
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Role = "x" })));
    }

    [Fact]
    public void ParseOrNull_InvalidActorId_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { ActorId = "a\u0000b" })));

    [Fact]
    public void ParseOrNull_GarbageOrEmpty_ReturnsNull()
    {
        Assert.Null(AccessLogWriterService.ParseOrNull(null));
        Assert.Null(AccessLogWriterService.ParseOrNull([]));
        Assert.Null(AccessLogWriterService.ParseOrNull("{not json"u8.ToArray()));
        Assert.Null(AccessLogWriterService.ParseOrNull("{\"event\":\"nope\"}"u8.ToArray()));
    }

    [Fact]
    public void ParseOrNull_HistoryReadWithoutCount_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Count = null })));

    [Fact]
    public void ParseOrNull_DefaultTime_ReturnsNull() =>
        Assert.Null(AccessLogWriterService.ParseOrNull(Bytes(Read() with { Time = default })));

    [Fact]
    public void ParseOrNull_MissingTime_ReturnsNull()
    {
        var node = JsonNode.Parse(Bytes(Read()))!.AsObject();
        Assert.True(node.Remove("time"));
        Assert.Null(AccessLogWriterService.ParseOrNull(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }
}
