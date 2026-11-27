using Dotwire.Configuration;
using Xunit;

namespace dotwire.Tests;

public class AccessLogRulesTests
{
    private static void Validate(AccessLogOptions a, NatsOptions? n = null, AuthOptions? au = null, ComplianceOptions? c = null) =>
        AccessLogRules.Validate(a, n ?? new NatsOptions(), au ?? new AuthOptions(), c ?? new ComplianceOptions());

    [Fact]
    public void Enabled_WithNatsDisabled_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            Validate(new AccessLogOptions { Enabled = true }, new NatsOptions { Enabled = false }));

    [Fact]
    public void Enabled_WithUncappedTokens_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            Validate(new AccessLogOptions { Enabled = true }, au: new AuthOptions { MaxTokenLifetimeMinutes = 0 }));

    [Fact]
    public void RetentionBelowMinimumKeep_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            Validate(new AccessLogOptions { RetentionDays = 30 }, c: new ComplianceOptions { MinimumKeepDays = 365 }));

    [Fact]
    public void RetentionZero_WithMinimumKeep_Passes() =>
        Validate(new AccessLogOptions { RetentionDays = 0 }, c: new ComplianceOptions { MinimumKeepDays = 365 });

    [Theory]
    [InlineData(-1)]
    [InlineData(36501)]
    public void RetentionOutOfRange_Throws(int days) =>
        Assert.Throws<InvalidOperationException>(() => Validate(new AccessLogOptions { RetentionDays = days }));

    [Fact]
    public void Disabled_WithNatsOff_Passes() =>
        Validate(new AccessLogOptions(), new NatsOptions { Enabled = false }, new AuthOptions { MaxTokenLifetimeMinutes = 0 });
}
