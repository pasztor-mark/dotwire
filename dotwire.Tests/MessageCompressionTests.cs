using Dotwire.Data;
using Xunit;

namespace dotwire.Tests;

public class MessageCompressionTests
{
    [Theory]
    [InlineData("timescale", "2.31.0", true)]
    [InlineData("timescale", "2.11.0", true)]
    [InlineData("timescale", "2.14.0-dev", true)]
    [InlineData("timescale", "2.10.3", false)]
    [InlineData("timescale", "2.9.0", false)]
    [InlineData("apache", "2.31.0", false)]
    [InlineData(null, "2.31.0", false)]
    [InlineData("timescale", null, false)]
    [InlineData("timescale", "garbage", false)]
    public void Supported_RequiresTimescaleLicenseAndAtLeast2_11(string? license, string? version, bool expected) =>
        Assert.Equal(expected, MessageCompression.Supported(license, version));
}
