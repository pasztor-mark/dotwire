using Dotwire.Configuration;
using Xunit;

namespace dotwire.Tests;

public class CompressionRulesTests
{
    [Fact] public void Unset_Passes() => CompressionRules.Validate(new CompressionOptions(), new NatsOptions());
    [Fact] public void Zero_Passes() => CompressionRules.Validate(new CompressionOptions { AfterDays = 0 }, new NatsOptions());
    [Fact] public void ThreeDays_DefaultStreamAge_Passes() => CompressionRules.Validate(new CompressionOptions { AfterDays = 3 }, new NatsOptions());
    [Fact] public void OneDay_TwelveHourStream_Passes() => CompressionRules.Validate(new CompressionOptions { AfterDays = 1 }, new NatsOptions { RoomsMaxAgeHours = 12 });
    [Fact] public void MaxDays_Passes() => CompressionRules.Validate(new CompressionOptions { AfterDays = 36500 }, new NatsOptions());

    [Theory]
    [InlineData(-1)]
    [InlineData(36501)]
    public void OutOfRange_Throws(int days)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompressionRules.Validate(new CompressionOptions { AfterDays = days }, new NatsOptions()));
        Assert.Contains("Dotwire:Compression:AfterDays", ex.Message);
        Assert.Contains("0..36500", ex.Message);
    }

    [Fact]
    public void TwoDays_DefaultStreamAge_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompressionRules.Validate(new CompressionOptions { AfterDays = 2 }, new NatsOptions()));
        Assert.Contains("Dotwire:Compression:AfterDays", ex.Message);
        Assert.Contains("Nats:RoomsMaxAgeHours", ex.Message);
        Assert.Contains("48", ex.Message);
    }
}
