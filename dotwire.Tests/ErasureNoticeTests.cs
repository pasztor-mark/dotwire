using System.Text.Json;
using Dotwire.Api;
using Dotwire.Configuration;
using Dotwire.Nats;
using Microsoft.Extensions.Options;
using Xunit;

namespace dotwire.Tests;

public class ErasureNoticeTests
{
    [Fact]
    public void UserErasedPartialNotice_RoundTripsWithCamelCaseFields()
    {
        var roomA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var roomB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var notice = new ErasureNotice(
            "user.erased",
            DateTimeOffset.Parse("2026-11-26T10:00:00Z"),
            UserId: "user-1",
            Mode: "anonymize",
            ErasureId: 7,
            Status: "partial",
            Messages: [new ErasedMessageRef(roomA, 1), new ErasedMessageRef(roomB, 2)]);

        var json = JsonSerializer.SerializeToUtf8Bytes(notice, DotwireJsonContext.Default.ErasureNotice);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("user.erased", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(roomA, doc.RootElement.GetProperty("messages")[0].GetProperty("roomId").GetGuid());
        Assert.Equal(1UL, doc.RootElement.GetProperty("messages")[0].GetProperty("seq").GetUInt64());

        var back = JsonSerializer.Deserialize(json, DotwireJsonContext.Default.ErasureNotice);
        Assert.NotNull(back);
        Assert.Equal("partial", back.Status);
        Assert.Equal(2, back.Messages!.Length);
        Assert.Equal(new ErasedMessageRef(roomB, 2), back.Messages[1]);
    }

    [Fact]
    public async Task Publisher_IsNoOpWithoutErasureUrl()
    {
        var publisher = new ErasureNoticePublisher(
            null!,
            Options.Create(new NatsOptions { Enabled = true }),
            Options.Create(new WebhooksOptions()));

        Assert.False(publisher.IsEnabled);
        await publisher.PublishAsync(new ErasureNotice("message.redacted", DateTimeOffset.UtcNow), CancellationToken.None);
    }

    [Fact]
    public void WebhookUrlRules_RejectsInsecureErasureUrl()
    {
        var options = new WebhooksOptions();
        options.Erasure.Url = "http://example.com/erasure";

        var ex = Assert.Throws<InvalidOperationException>(() => WebhookUrlRules.Validate(options));
        Assert.Contains("Dotwire:Webhooks:Erasure:Url", ex.Message);
    }
}
