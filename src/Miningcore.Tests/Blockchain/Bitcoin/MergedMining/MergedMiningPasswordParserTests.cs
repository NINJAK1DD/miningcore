using Miningcore.Blockchain.Bitcoin.MergedMining;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningPasswordParserTests
{
    [Theory]
    [InlineData("doge")]
    [InlineData("aux")]
    [InlineData("doge-address")]
    public void IsValidAddressParameter_AcceptsUnambiguousKeys(string key)
    {
        Assert.True(MergedMiningPasswordParser.IsValidAddressParameter(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("d")]
    [InlineData("D")]
    [InlineData("doge;aux")]
    [InlineData("doge=aux")]
    public void IsValidAddressParameter_RejectsReservedOrDelimitedKeys(string key)
    {
        Assert.False(MergedMiningPasswordParser.IsValidAddressParameter(key));
    }

    [Theory]
    [InlineData("d=65536;doge=DJ7zExampleAddress", "DJ7zExampleAddress")]
    [InlineData(" DOGE = DJ7zExampleAddress ; d = 1024 ", "DJ7zExampleAddress")]
    [InlineData("d=1;foo=bar;DoGe=DJ7zExampleAddress", "DJ7zExampleAddress")]
    public void GetValue_ReturnsCaseInsensitiveTrimmedValue(string password, string expected)
    {
        Assert.Equal(expected, MergedMiningPasswordParser.GetValue(password, "doge"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("d=65536")]
    [InlineData("dogecoin=DJ7zExampleAddress")]
    [InlineData("doge=")]
    [InlineData("doge")]
    public void GetValue_ReturnsNullWhenValueIsMissing(string password)
    {
        Assert.Null(MergedMiningPasswordParser.GetValue(password, "doge"));
    }

    [Fact]
    public void GetValue_ReturnsFirstMatchingValue()
    {
        Assert.Equal("first", MergedMiningPasswordParser.GetValue("doge=first;doge=second", "doge"));
    }
}
