using Miningcore.Blockchain.Bitcoin.MergedMining;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningPasswordParserTests
{
    [Fact]
    public void GetValue_ExtractsAuxiliaryAddress()
    {
        var result = MergedMiningPasswordParser.GetValue("d=65536;doge=DExampleAddress", "doge");
        Assert.Equal("DExampleAddress", result);
    }

    [Fact]
    public void GetValue_IsCaseInsensitive()
    {
        var result = MergedMiningPasswordParser.GetValue("d=1024;DOGE=DExampleAddress", "doge");
        Assert.Equal("DExampleAddress", result);
    }

    [Fact]
    public void GetValue_ReturnsNullWhenKeyIsMissing()
    {
        Assert.Null(MergedMiningPasswordParser.GetValue("d=1024", "doge"));
    }
}
