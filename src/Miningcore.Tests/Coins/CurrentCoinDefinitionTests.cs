using System.IO;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Coins;

public class CurrentCoinDefinitionTests : TestBase
{
    [Fact]
    public void StakeCube_UsesCurrentSccPowDefinition()
    {
        var template = Assert.IsType<ProgpowCoinTemplate>(
            ModuleInitializer.CoinTemplates["stakecube"]);

        Assert.Equal("sccpow", template.Progpower);
    }

    [Fact]
    public void Zetacoin_UsesCurrentScryptProofOfWork()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["zetacoin"]);

        Assert.IsType<Scrypt>(template.HeaderHasherValue);
        var blockHasher = Assert.IsType<DigestReverser>(template.BlockHasherValue);
        Assert.IsType<Sha256D>(blockHasher.Upstream);
        Assert.Null(template.PoSBlockHasher);
        Assert.Equal(65536d, template.ShareMultiplier);
    }

    [Fact]
    public void Loader_RejectsDuplicateCoinIdentifiersWithinOneFile()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path,
                """
                {
                    "duplicate": {
                        "name": "First",
                        "symbol": "ONE",
                        "family": "bitcoin"
                    },
                    "duplicate": {
                        "name": "Second",
                        "symbol": "TWO",
                        "family": "bitcoin"
                    }
                }
                """);

            var ex = Assert.Throws<JsonReaderException>(() =>
                CoinTemplateLoader.Load(container, new[] { path }));

            Assert.Contains("duplicate", ex.Message.ToLowerInvariant());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loader_AllowsExplicitRedefinitionAcrossFiles()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(firstPath,
                """
                {
                    "override": {
                        "name": "First",
                        "symbol": "ONE",
                        "family": "bitcoin"
                    }
                }
                """);
            File.WriteAllText(secondPath,
                """
                {
                    "override": {
                        "name": "Second",
                        "symbol": "TWO",
                        "family": "bitcoin"
                    }
                }
                """);

            var templates = CoinTemplateLoader.Load(container,
                new[] { firstPath, secondPath });

            Assert.Equal("Second", templates["override"].Name);
            Assert.Equal(secondPath, templates["override"].Source);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }
}
