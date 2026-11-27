using Dotwire.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace dotwire.Tests;

public class ComplianceRulesTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        ComplianceRules.Validate(new ComplianceOptions(), new RetentionOptions { MessagesDays = null });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(36501)]
    public void MinimumKeepDays_OutOfRange_Throws(int days)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ComplianceRules.Validate(new ComplianceOptions { MinimumKeepDays = days }, new RetentionOptions()));
        Assert.Contains("Dotwire:Compliance:MinimumKeepDays", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void RecheckMinutes_OutOfRange_Throws(int minutes)
    {
        var options = new ComplianceOptions();
        options.Erasure.RecheckMinutes = minutes;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ComplianceRules.Validate(options, new RetentionOptions()));
        Assert.Contains("Dotwire:Compliance:Erasure:RecheckMinutes", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void HoldRetentionWaitSeconds_OutOfRange_Throws(int seconds)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ComplianceRules.Validate(new ComplianceOptions { HoldRetentionWaitSeconds = seconds }, new RetentionOptions()));
        Assert.Contains("Dotwire:Compliance:HoldRetentionWaitSeconds", ex.Message);
    }

    [Fact]
    public void Retention_BelowMinimumKeep_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ComplianceRules.Validate(
                new ComplianceOptions { MinimumKeepDays = 365 },
                new RetentionOptions { MessagesDays = 30 }));
        Assert.Contains("Dotwire:Retention:MessagesDays", ex.Message);
        Assert.Contains("Dotwire:Compliance:MinimumKeepDays", ex.Message);
    }

    [Fact]
    public void Retention_Zero_WithMinimumKeep_IsValid()
    {
        ComplianceRules.Validate(
            new ComplianceOptions { MinimumKeepDays = 365 },
            new RetentionOptions { MessagesDays = 0 });
    }

    [Fact]
    public void Retention_EqualToMinimumKeep_IsValid()
    {
        ComplianceRules.Validate(
            new ComplianceOptions { MinimumKeepDays = 365 },
            new RetentionOptions { MessagesDays = 365 });
    }

    [Theory]
    [InlineData("anonymize", ErasureMode.Anonymize)]
    [InlineData("Anonymize", ErasureMode.Anonymize)]
    [InlineData("delete", ErasureMode.Delete)]
    [InlineData("DELETE", ErasureMode.Delete)]
    public void ErasureModes_TryParse_IsCaseInsensitive(string input, ErasureMode expected)
    {
        Assert.True(ErasureModes.TryParse(input, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shred")]
    public void ErasureModes_TryParse_RejectsUnknown(string? input)
    {
        Assert.False(ErasureModes.TryParse(input, out _));
    }

    [Theory]
    [InlineData(ErasureMode.Anonymize, "anonymize")]
    [InlineData(ErasureMode.Delete, "delete")]
    public void ErasureModes_ToWire_UsesLowercaseNames(ErasureMode mode, string wire)
    {
        Assert.Equal(wire, ErasureModes.ToWire(mode));
    }

    [Fact]
    public void UnknownMode_BindingFails_NamingTheKey()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Dotwire:Compliance:Erasure:Mode"] = "shred" })
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.GetSection(ComplianceOptions.SectionName).Get<ComplianceOptions>());
        Assert.Contains("Dotwire:Compliance:Erasure:Mode", ex.Message);
    }
}
