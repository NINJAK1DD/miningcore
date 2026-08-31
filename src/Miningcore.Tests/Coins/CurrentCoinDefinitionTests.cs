using System;
using System.IO;
using System.Linq;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Mining;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Coins;

public class CurrentCoinDefinitionTests : TestBase
{
    [Fact]
    public void DigiByte_AdvertisesCurrentOdoCryptInsteadOfRetiredGroestl()
    {
        Assert.Equal(new[]
        {
            "digibyte-odocrypt",
            "digibyte-qubit",
            "digibyte-scrypt",
            "digibyte-sha256",
            "digibyte-skein",
        }, ModuleInitializer.CoinTemplates.Keys
            .Where(x => x.StartsWith("digibyte-", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal));

        var template = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["digibyte-odocrypt"]);

        Assert.IsType<OdoCrypt>(template.HeaderHasherValue);
        Assert.IsType<Sha256D>(template.CoinbaseHasherValue);
        var blockHasher = Assert.IsType<DigestReverser>(
            template.BlockHasherValue);
        Assert.IsType<Sha256D>(blockHasher.Upstream);
        Assert.Equal(1d, template.ShareMultiplier);
        Assert.Equal("odo", template.BlockTemplateRpcExtraParams[0]?.Value<string>());
        Assert.Equal(0x1fffe000u, template.AllowedVersionRollingMask);
        Assert.Equal(0x00000f00u, template.VersionRollingConsensusMask);
        Assert.Equal(9112320u,
            template.Networks["main"].OdoCryptActivationHeight);
        Assert.Equal(864000u,
            template.Networks["main"].OdoCryptShapeChangeInterval);
        Assert.Equal(500u,
            template.Networks["test"].OdoCryptActivationHeight);
        Assert.Equal(86400u,
            template.Networks["test"].OdoCryptShapeChangeInterval);
        Assert.Equal(600u,
            template.Networks["signet"].OdoCryptActivationHeight);
        Assert.Equal(86400u,
            template.Networks["signet"].OdoCryptShapeChangeInterval);
        Assert.Equal(600u,
            template.Networks["regtest"].OdoCryptActivationHeight);
        Assert.Equal(864000u,
            template.Networks["regtest"].OdoCryptShapeChangeInterval);
        Assert.Same(template.Networks["signet"],
            template.GetNetwork(new ChainName("signet")));
    }

    [Fact]
    public void Loader_AcceptsCanonicalOdoCryptSchedule()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            LoadOdoCryptTemplate(CreateOdoCryptNetworks()));

        Assert.Equal(864000u,
            template.Networks["main"].OdoCryptShapeChangeInterval);
        Assert.Equal(9112320u,
            template.Networks["main"].OdoCryptActivationHeight);
        Assert.Equal(86400u,
            template.Networks["test"].OdoCryptShapeChangeInterval);
    }

    [Fact]
    public void Loader_RecognizesMixedCaseOdoCryptHasherAndRequiresContract()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            LoadOdoCryptTemplate(CreateOdoCryptNetworks(), "OdoCrypt"));

        Assert.IsType<OdoCrypt>(template.HeaderHasherValue);

        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadOdoCryptTemplate(null, "OdoCrypt"));

        Assert.Contains("requires typed network activation", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OdoCryptShapeChangeInterval", 864000, "canonically cased")]
    [InlineData("odoCryptShapeChangeInterval", 0, "nonzero")]
    [InlineData("odoCryptShapeChangeInterval", -1, "unsigned")]
    [InlineData("odoCryptShapeChangeInterval", "864000", "integer")]
    public void Loader_RejectsMalformedOdoCryptSchedule(string propertyName,
        object value, string expectedDiagnostic)
    {
        var networks = CreateOdoCryptNetworks();
        var main = Assert.IsType<JObject>(networks["main"]);
        main.Remove("odoCryptShapeChangeInterval");
        main[propertyName] = JToken.FromObject(value);

        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadOdoCryptTemplate(networks));

        Assert.Contains("odocrypt-test", ex.Message, StringComparison.Ordinal);
        Assert.Contains("main", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDiagnostic, ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OdoCryptActivationHeight", 9112320, "canonically cased")]
    [InlineData("odoCryptActivationHeight", 0, "nonzero")]
    [InlineData("odoCryptActivationHeight", -1, "unsigned")]
    [InlineData("odoCryptActivationHeight", "9112320", "integer")]
    public void Loader_RejectsMalformedOdoCryptActivation(string propertyName,
        object value, string expectedDiagnostic)
    {
        var networks = CreateOdoCryptNetworks();
        var main = Assert.IsType<JObject>(networks["main"]);
        main.Remove("odoCryptActivationHeight");
        main[propertyName] = JToken.FromObject(value);

        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadOdoCryptTemplate(networks));

        Assert.Contains("odocrypt-test", ex.Message, StringComparison.Ordinal);
        Assert.Contains("main", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDiagnostic, ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loader_RejectsMissingOdoCryptNetworkSchedule()
    {
        var networks = CreateOdoCryptNetworks();
        networks.Remove("test");

        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadOdoCryptTemplate(networks));

        Assert.Contains("requires a 'test' network object", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsOdoCryptScheduleOnAnotherHasher()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadOdoCryptTemplate(CreateOdoCryptNetworks(), "sha256d"));

        Assert.Contains("valid only", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("odocrypt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsOdoCryptScheduleOutsideNetworkScope()
    {
        var networks = CreateOdoCryptNetworks();
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadOdoCryptTemplate(networks, extraTemplateProperty:
                new JProperty("odoCryptShapeChangeInterval", 864000)));

        Assert.Contains("may appear only", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

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

    [Theory]
    [InlineData("blockSerializer", false)]
    [InlineData("BlockSerializer", true)]
    [InlineData("BLOCKSERIALIZER", false)]
    public void Loader_RejectsInertBlockSerializerMetadata(string propertyName,
        bool nullValue)
    {
        var path = Path.GetTempFileName();
        var document = new JObject
        {
            ["inert-template"] = new JObject
            {
                ["name"] = "Inert Template",
                ["symbol"] = "INERT",
                ["family"] = "bitcoin",
                [propertyName] = nullValue
                    ? JValue.CreateNull()
                    : JValue.CreateString("legacy"),
            },
        };

        try
        {
            File.WriteAllText(path, document.ToString());

            var ex = Assert.Throws<PoolStartupException>(() =>
                CoinTemplateLoader.Load(container, new[] { path }));

            Assert.Contains("inert-template", ex.Message,
                StringComparison.Ordinal);
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.Contains(propertyName, ex.Message, StringComparison.Ordinal);
            Assert.Contains("unsupported", ex.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no runtime effect", ex.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BitcoinTemplate_DoesNotExposeInertBlockSerializerProperty()
    {
        Assert.Null(typeof(BitcoinTemplate).GetProperty("BlockSerializer"));
    }

    [Fact]
    public void Loader_AcceptsDisjointVersionRollingAndConsensusMasks()
    {
        var template = Assert.IsType<BitcoinTemplate>(
            LoadVersionRollingTemplate(new JObject
            {
                ["versionRollingMask"] = "0x1fff2000",
                ["versionRollingConsensusMask"] = "0x0000c000",
            }));

        Assert.Equal(0x1fff2000u, template.AllowedVersionRollingMask);
        Assert.Equal(0x0000c000u, template.VersionRollingConsensusMask);
    }

    [Theory]
    [InlineData("versionRollingMask", "0x0000c000",
        "versionRollingConsensusMask", "0x0000c000", "overlaps")]
    [InlineData("versionRollingMask", "0x20000000",
        null, null, "outside")]
    [InlineData("versionRollingMask", "0x00000000",
        null, null, "nonzero")]
    [InlineData("versionRollingConsensusMask", "0x00000000",
        "disableVersionRolling", "true", "nonzero")]
    [InlineData("versionRollingConsensusMask", "0x0000c000",
        null, null, "requires")]
    public void Loader_RejectsUnsafeVersionRollingContracts(string firstName,
        string firstValue, string secondName, string secondValue,
        string expectedDiagnostic)
    {
        var properties = new JObject { [firstName] = firstValue };

        if(secondName != null)
        {
            properties[secondName] = bool.TryParse(secondValue,
                out var booleanValue)
                ? booleanValue
                : secondValue;
        }

        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(properties));

        Assert.Contains(expectedDiagnostic, ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loader_RejectsVersionRollingMaskWhenNegotiationIsDisabled()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["disableVersionRolling"] = true,
                ["versionRollingMask"] = "0x1fffe000",
            }));

        Assert.Contains("cannot be combined", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("VersionRollingMask", "0x1fffe000", "exact casing")]
    [InlineData("versionRollingMask", "1fffe000", "eight-digit")]
    [InlineData("versionRollingMask", "0x1fffe00", "eight-digit")]
    [InlineData("versionRollingMask", "0X1fffe000", "eight-digit")]
    [InlineData("versionRollingMask", 536862720, "eight-digit")]
    [InlineData("versionRollingMask", null, "eight-digit")]
    [InlineData("versionRollingConsensusMask", "0xC000", "eight-digit")]
    public void Loader_RejectsNonCanonicalVersionRollingMaskSyntax(
        string propertyName, object value, string expectedDiagnostic)
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                [propertyName] = value == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(value),
            }));

        Assert.Contains(expectedDiagnostic, ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("versionRollingMask", false)]
    [InlineData("versionRollingMask", true)]
    [InlineData("versionRollingConsensusMask", false)]
    [InlineData("versionRollingConsensusMask", true)]
    public void Loader_RejectsStructuredVersionRollingMaskWithNamedDiagnostic(
        string propertyName, bool arrayValue)
    {
        var path = Path.GetTempFileName();
        var template = new JObject
        {
            ["name"] = "Version Rolling Test",
            ["symbol"] = "VRT",
            ["family"] = "bitcoin",
            [propertyName] = arrayValue ? new JArray() : new JObject(),
        };

        try
        {
            File.WriteAllText(path, new JObject
            {
                ["version-rolling-test"] = template,
            }.ToString());

            var ex = Assert.Throws<PoolStartupException>(() =>
                CoinTemplateLoader.Load(container, new[] { path }));

            Assert.Contains("version-rolling-test", ex.Message,
                StringComparison.Ordinal);
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.Contains(propertyName, ex.Message, StringComparison.Ordinal);
            Assert.Contains("eight-digit", ex.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.IsNotType<InvalidCastException>(ex.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loader_RejectsCaseVariantVersionRollingMaskDuplicates()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["versionRollingMask"] = "0x1fffe000",
                ["VersionRollingMask"] = "0x1fff2000",
            }));

        Assert.Contains("ambiguous", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loader_RejectsWrongCaseDisableVersionRolling()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["DisableVersionRolling"] = true,
            }));

        Assert.Contains("exact casing", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DisableVersionRolling", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsCaseVariantDisableVersionRollingDuplicates()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["disableVersionRolling"] = true,
                ["DisableVersionRolling"] = false,
            }));

        Assert.Contains("ambiguous", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disableVersionRolling", ex.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true")]
    [InlineData(1)]
    [InlineData(null)]
    public void Loader_RejectsNonBooleanDisableVersionRolling(object value)
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["disableVersionRolling"] = value == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(value),
            }));

        Assert.Contains("disableVersionRolling", ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("JSON Boolean", ex.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loader_RejectsStructuredDisableVersionRolling(bool arrayValue)
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["disableVersionRolling"] = arrayValue
                    ? new JArray()
                    : new JObject(),
            }));

        Assert.Contains("disableVersionRolling", ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("JSON Boolean", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsVersionRollingMaskOnOtherCoinFamilies()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["versionRollingMask"] = "0x1fffe000",
            }, "equihash"));

        Assert.Contains("Bitcoin Stratum runtime", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("handshake")]
    [InlineData("nexa")]
    [InlineData("satoshicash")]
    public void Loader_RejectsVersionRollingPolicyForSharedTemplateTypesWithoutRuntime(
        string family)
    {
        var policies = new (string Name, JToken Value)[]
        {
            ("versionRollingMask", "0x1fffe000"),
            ("versionRollingConsensusMask", "0x00000e00"),
            ("disableVersionRolling", true),
        };

        foreach(var policy in policies)
        {
            var ex = Assert.Throws<PoolStartupException>(() =>
                LoadVersionRollingTemplate(new JObject
                {
                    [policy.Name] = policy.Value,
                }, family));

            Assert.Contains("Bitcoin Stratum runtime", ex.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Loader_PrioritizesFamilyDiagnosticForStructuredForeignMask()
    {
        var ex = Assert.Throws<PoolStartupException>(() =>
            LoadVersionRollingTemplate(new JObject
            {
                ["versionRollingMask"] = new JObject(),
            }, "equihash"));

        Assert.Contains("Bitcoin Stratum runtime", ex.Message,
            StringComparison.OrdinalIgnoreCase);
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

    private CoinTemplate LoadVersionRollingTemplate(JObject properties,
        string family = "bitcoin")
    {
        var path = Path.GetTempFileName();
        var template = new JObject
        {
            ["name"] = "Version Rolling Test",
            ["symbol"] = "VRT",
            ["family"] = family,
        };

        foreach(var property in properties.Properties())
            template.Add(property.Name, property.Value);

        try
        {
            File.WriteAllText(path, new JObject
            {
                ["version-rolling-test"] = template,
            }.ToString());

            return CoinTemplateLoader.Load(container,
                new[] { path })["version-rolling-test"];
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JObject CreateOdoCryptNetworks() => new()
    {
        ["main"] = new JObject
        {
            ["odoCryptActivationHeight"] = 9112320,
            ["odoCryptShapeChangeInterval"] = 864000,
        },
        ["test"] = new JObject
        {
            ["odoCryptActivationHeight"] = 500,
            ["odoCryptShapeChangeInterval"] = 86400,
        },
        ["signet"] = new JObject
        {
            ["odoCryptActivationHeight"] = 600,
            ["odoCryptShapeChangeInterval"] = 86400,
        },
        ["regtest"] = new JObject
        {
            ["odoCryptActivationHeight"] = 600,
            ["odoCryptShapeChangeInterval"] = 864000,
        },
    };

    private CoinTemplate LoadOdoCryptTemplate(JObject networks,
        string headerHasher = "odocrypt",
        JProperty extraTemplateProperty = null)
    {
        var path = Path.GetTempFileName();

        try
        {
            var template = new JObject
            {
                ["name"] = "Odocrypt Test",
                ["symbol"] = "ODO",
                ["family"] = "bitcoin",
                ["headerHasher"] = new JObject
                {
                    ["hash"] = headerHasher,
                },
                ["networks"] = networks,
            };

            if(extraTemplateProperty != null)
                template.Add(extraTemplateProperty);

            File.WriteAllText(path, new JObject
            {
                ["odocrypt-test"] = template,
            }.ToString());

            return CoinTemplateLoader.Load(container,
                new[] { path })["odocrypt-test"];
        }
        finally
        {
            File.Delete(path);
        }
    }
}
