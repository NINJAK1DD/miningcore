using System.IO;
using System.Linq;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;
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

        var input = Enumerable.Repeat((byte) 0x80, 32).ToArray();
        var output = new byte[32];

        template.HeaderHasherValue.Digest(input, output);

        Assert.Equal(
            "b546d334422ff5fff98e8ba847a55bbc06271c64bb5e21107b1b225f6579d40a",
            output.ToHexString());

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

            var ex = Assert.Throws<PoolStartupException>(() =>
                CoinTemplateLoader.Load(container, new[] { path }));

            Assert.Contains("duplicate", ex.Message.ToLowerInvariant());
            Assert.Contains(path, ex.Message);
            Assert.IsType<Newtonsoft.Json.JsonReaderException>(ex.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loader_RejectsDuplicateNestedPropertiesWithinOneFile()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path,
                """
                {
                    "duplicate-nested": {
                        "name": "Duplicate Nested",
                        "symbol": "DUP",
                        "family": "bitcoin",
                        "headerHasher": {
                            "hash": "scrypt",
                            "hash": "sha256d"
                        }
                    }
                }
                """);

            var ex = Assert.Throws<PoolStartupException>(() =>
                CoinTemplateLoader.Load(container, new[] { path }));

            Assert.Contains("hash", ex.Message.ToLowerInvariant());
            Assert.Contains(path, ex.Message);
            Assert.IsType<Newtonsoft.Json.JsonReaderException>(ex.InnerException);
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

    [Theory]
    [InlineData("2025-01-01")]
    [InlineData("2026-08-16T15:30:00Z")]
    [InlineData("2026-08-16T15:30:00+01:00")]
    [InlineData("2026-08-16T15:30:00")]
    public void Loader_PreservesDateLookingExtensionStrings(
        string configuredValue)
    {
        var path = Path.GetTempFileName();
        var document = new JObject
        {
            ["date-test"] = new JObject
            {
                ["name"] = "Date Test",
                ["symbol"] = "DATE",
                ["family"] = "bitcoin",
                ["extensionValue"] = configuredValue,
                ["networks"] = new JObject
                {
                    ["main"] = new JObject
                    {
                        ["networkExtensionValue"] = configuredValue,
                    },
                },
            },
        };

        try
        {
            File.WriteAllText(path, document.ToString());

            var template = Assert.IsType<BitcoinTemplate>(
                CoinTemplateLoader.Load(container, new[] { path })["date-test"]);

            Assert.Equal(configuredValue,
                Assert.IsType<string>(template.Extra["extensionValue"]));
            Assert.Equal(configuredValue, Assert.IsType<string>(
                template.Networks["main"].Extra["networkExtensionValue"]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
