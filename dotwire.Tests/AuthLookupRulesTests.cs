using Dotwire.Auth;
using Dotwire.Configuration;
using Xunit;

namespace dotwire.Tests;

public class AuthLookupRulesTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void CacheSeconds_OutOfRange_Throws(int seconds)
    {
        var options = new DotwireOptions { AuthCacheSeconds = seconds };

        var ex = Assert.Throws<InvalidOperationException>(() => AuthLookupRules.Validate(options));
        Assert.Contains("Dotwire:AuthCacheSeconds", ex.Message);
        Assert.Contains("0..60", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void CacheSeconds_Bounds_Pass(int seconds)
    {
        AuthLookupRules.Validate(new DotwireOptions { AuthCacheSeconds = seconds });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void MaxInFlight_OutOfRange_Throws(int maxInFlight)
    {
        var options = new DotwireOptions { AuthBatching = new AuthBatchingOptions { MaxInFlight = maxInFlight } };

        var ex = Assert.Throws<InvalidOperationException>(() => AuthLookupRules.Validate(options));
        Assert.Contains("Dotwire:AuthBatching:MaxInFlight", ex.Message);
        Assert.Contains("1..64", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void MaxInFlight_Bounds_Pass(int maxInFlight)
    {
        AuthLookupRules.Validate(new DotwireOptions { AuthBatching = new AuthBatchingOptions { MaxInFlight = maxInFlight } });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void MaxBatchSize_OutOfRange_Throws(int maxBatchSize)
    {
        var options = new DotwireOptions { AuthBatching = new AuthBatchingOptions { MaxBatchSize = maxBatchSize } };

        var ex = Assert.Throws<InvalidOperationException>(() => AuthLookupRules.Validate(options));
        Assert.Contains("Dotwire:AuthBatching:MaxBatchSize", ex.Message);
        Assert.Contains("1..4096", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void MaxBatchSize_Bounds_Pass(int maxBatchSize)
    {
        AuthLookupRules.Validate(new DotwireOptions { AuthBatching = new AuthBatchingOptions { MaxBatchSize = maxBatchSize } });
    }

    [Fact]
    public void Defaults_AreOffAndValid()
    {
        var options = new DotwireOptions();

        Assert.False(options.AuthBatching.Enabled);
        Assert.Equal(2, options.AuthBatching.MaxInFlight);
        Assert.Equal(512, options.AuthBatching.MaxBatchSize);
        Assert.Equal(0, options.AuthCacheSeconds);
        AuthLookupRules.Validate(options);
    }
}
