using System.Collections.Generic;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Coins;

public class ScryptCoinTemplateTests : TestBase
{
    public static IEnumerable<object[]> SupportedTemplates()
    {
        yield return new object[] { "blockchaincoinx", "XCCX" };
        yield return new object[] { "catcoin", "CAT" };
        yield return new object[] { "cyberyen", "CY" };
        yield return new object[] { "ferrite", "FEC" };
        yield return new object[] { "ibithub", "IBH" };
        yield return new object[] { "litecoin-ii", "LC2" };
        yield return new object[] { "mateablecoin-scrypt", "MTBC" };
        yield return new object[] { "stohncoin", "SOH" };
        yield return new object[] { "theminerzcoin", "TMC" };
        yield return new object[] { "b1t", "B1T" };
        yield return new object[] { "bells", "BEL" };
        yield return new object[] { "bonkcoin", "BONC" };
        yield return new object[] { "craftcoin", "CRC" };
        yield return new object[] { "dingocoin", "DINGO" };
        yield return new object[] { "earthcoin", "EAC" };
        yield return new object[] { "flopcoin", "FLOP" };
        yield return new object[] { "junkcoin", "JKC" };
        yield return new object[] { "luckycoin", "LKY" };
        yield return new object[] { "newyorkcoin", "NYC" };
        yield return new object[] { "pepecoin", "PEP" };
        yield return new object[] { "shibainucoin", "SHIC" };
        yield return new object[] { "trumpow", "TRMP" };
    }

    [Theory]
    [MemberData(nameof(SupportedTemplates))]
    public void Template_UsesBitcoinCompatibleScryptContract(string key,
        string symbol)
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates[key]);

        Assert.Equal(symbol, template.Symbol);
        Assert.Equal(CoinFamily.Bitcoin, template.Family);
        Assert.StartsWith("https://github.com/", template.Github);
        Assert.IsType<Sha256D>(template.CoinbaseHasherValue);
        Assert.IsType<Scrypt>(template.HeaderHasherValue);
        var blockHasher = Assert.IsType<DigestReverser>(
            template.BlockHasherValue);
        Assert.IsType<Sha256D>(blockHasher.Upstream);
        Assert.Equal(65536d, template.ShareMultiplier);
    }

    [Theory]
    [InlineData("cyberyen")]
    [InlineData("ferrite")]
    [InlineData("litecoin-ii")]
    public void MwebCapableTemplate_RequestsAndSerializesMweb(string key)
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates[key]);

        Assert.True(template.HasMWEB);
    }

    [Theory]
    [InlineData("catcoin")]
    [InlineData("newyorkcoin")]
    public void FutureMwebDeployment_DoesNotChangeCurrentTemplateRpc(string key)
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates[key]);

        Assert.False(template.HasMWEB);
    }

    [Theory]
    [InlineData("blockchaincoinx")]
    [InlineData("mateablecoin-scrypt")]
    [InlineData("theminerzcoin")]
    public void HybridTemplate_UsesScryptForProofOfStakeBlocks(string key)
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates[key]);
        var posHasher = Assert.IsType<DigestReverser>(
            template.PoSBlockHasherValue);

        Assert.IsType<Scrypt>(posHasher.Upstream);
    }

    [Fact]
    public void MateableCoinScrypt_PassesRequiredAlgorithmToGetBlockTemplate()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["mateablecoin-scrypt"]);
        var parameters = Assert.IsType<JArray>(
            template.BlockTemplateRpcExtraParams);

        Assert.Equal("scrypt", Assert.Single(parameters).Value<string>());
    }

    [Theory]
    [InlineData("b1t")]
    [InlineData("bonkcoin")]
    [InlineData("dingocoin")]
    [InlineData("flopcoin")]
    [InlineData("pepecoin")]
    [InlineData("shibainucoin")]
    [InlineData("trumpow")]
    public void MatureAuxPowTemplate_RetainsReorgSafetyMargin(string key)
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates[key]);

        Assert.Equal(251, template.CoinbaseMinConfimations);
    }

    [Fact]
    public void QuaiScrypt_IsNotAdvertisedAsBitcoinRpcCompatible()
    {
        Assert.False(ModuleInitializer.CoinTemplates.ContainsKey("quai-scrypt"));
    }
}
