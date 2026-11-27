using Dotwire.Nats;
using Xunit;

namespace dotwire.Tests;

public class ErasureWorkerRulesTests
{
    [Theory]
    [InlineData(0, -1000, true)]
    [InlineData(-1, -1000, true)]
    [InlineData(48, -1, true)]
    [InlineData(48, -49, false)]
    public void MayStillBeInStream_HonorsUnlimitedMaxAge(int maxAgeHours, int ageHours, bool expected)
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(expected, ErasureWorkerService.MayStillBeInStream(now.AddHours(ageHours), maxAgeHours, now));
    }
}
