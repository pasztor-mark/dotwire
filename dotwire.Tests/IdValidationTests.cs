using Dotwire.Auth;
using Xunit;

namespace dotwire.Tests;

public class IdValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsInvalid(string? id)
    {
        Assert.False(IdValidation.IsValid(id));
    }

    [Fact]
    public void MaxLength256_IsValid()
    {
        Assert.True(IdValidation.IsValid(new string('a', 256)));
    }

    [Fact]
    public void OverMaxLength257_IsInvalid()
    {
        Assert.False(IdValidation.IsValid(new string('a', 257)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    [InlineData("\r")]
    public void ControlCharacters_AreInvalid(string control)
    {
        Assert.False(IdValidation.IsValid($"user{control}1"));
    }

    [Fact]
    public void Del0x7F_IsInvalid()
    {
        Assert.False(IdValidation.IsValid("user1"));
    }

    [Theory]
    [InlineData("user-1")]
    [InlineData("a")]
    [InlineData("host-admin")]
    [InlineData("üser-ñame-日本語")]
    public void OrdinaryText_IsValid(string id)
    {
        Assert.True(IdValidation.IsValid(id));
    }
}
