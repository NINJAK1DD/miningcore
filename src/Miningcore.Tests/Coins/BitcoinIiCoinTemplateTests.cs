using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Xunit;

namespace Miningcore.Tests.Coins;

public class BitcoinIiCoinTemplateTests : TestBase
{
    [Fact]
    public void Template_UsesBitcoinCompatibleSha256dContract()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin-ii"]);

        Assert.Equal("Bitcoin II", template.Name);
        Assert.Equal("Bitcoin-II", template.CanonicalName);
        Assert.Equal("BC2", template.Symbol);
        Assert.Equal(CoinFamily.Bitcoin, template.Family);
        Assert.IsType<Sha256D>(template.CoinbaseHasherValue);
        Assert.IsType<Sha256D>(template.HeaderHasherValue);
        var blockHasher = Assert.IsType<DigestReverser>(
            template.BlockHasherValue);
        Assert.IsType<Sha256D>(blockHasher.Upstream);
        Assert.Contains("$height$", template.ExplorerBlockLink);
        Assert.Contains("{0}", template.ExplorerTxLink);
        Assert.Contains("{0}", template.ExplorerAccountLink);
    }

    [Fact]
    public void BlockHasher_MatchesMainnetGenesisBlock()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin-ii"]);
        var header =
            "010000000000000000000000000000000000000000000000000000000000000000000000" +
            "49dc6d26f9e122c04dadd0ded3bd72608705620301938bb8838f86cae9b4d180" +
            "ff075b67ffff001dd6c7325f";
        var hash = new byte[32];

        template.BlockHasherValue.Digest(header.HexToByteArray(), hash);

        Assert.Equal(
            "0000000028f062b221c1a8a5cf0244b1627315f7aa5b775b931cfec46dc17ceb",
            hash.ToHexString());
    }
}
