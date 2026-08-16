using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Miningcore.Api.Controllers;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Ergo.Configuration;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Configuration;
using Miningcore.Mining;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Api.Controllers;

public class PoolApiControllerTests
{
    // A sensitive-looking payment setting that is intentionally public belongs
    // here by fully-qualified member name. Keeping this separate from the
    // credential inventory makes a future TokenId-style false positive an
    // explicit reviewed decision instead of encouraging weakened detection.
    private static readonly HashSet<string>
        KnownBenignSensitivePaymentPropertyNames = new(StringComparer.Ordinal);

    [Fact]
    public void ToPoolInfo_UsesDedicatedBanningDtoWithoutAliasingConfiguration()
    {
        var source = new PoolShareBasedBanningConfig
        {
            Enabled = true,
            CheckThreshold = 25,
            InvalidPercent = 12.5,
            Time = 600,
            MinerEffortPercent = 250.25,
            MinerEffortTime = 900,
        };
        var config = CreateMinimalPoolConfig();
        config.Banning = source;
        var mapper = AutoMapperFactory.CreateMapper();

        var result = config.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        var banning = Assert.IsType<ApiPoolShareBasedBanningConfig>(
            result.ShareBasedBanning);
        Assert.NotSame(source, banning);
        Assert.True(banning.Enabled);
        Assert.Equal(25, banning.CheckThreshold);
        Assert.Equal(12.5, banning.InvalidPercent);
        Assert.Equal(600, banning.Time);
        Assert.Equal(250.25, banning.MinerEffortPercent);
        Assert.Equal(900, banning.MinerEffortTime);

        source.Enabled = false;
        source.CheckThreshold = 1;
        source.InvalidPercent = 2;
        source.Time = 3;
        source.MinerEffortPercent = null;
        source.MinerEffortTime = null;

        Assert.True(banning.Enabled);
        Assert.Equal(25, banning.CheckThreshold);
        Assert.Equal(12.5, banning.InvalidPercent);
        Assert.Equal(600, banning.Time);
        Assert.Equal(250.25, banning.MinerEffortPercent);
        Assert.Equal(900, banning.MinerEffortTime);
    }

    [Fact]
    public void BanningDto_ExposesExactlyTheExistingSixFieldContract()
    {
        var properties = typeof(ApiPoolShareBasedBanningConfig)
            .GetProperties()
            .OrderBy(property => property.Name)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.Equal(new[]
        {
            ("CheckThreshold", typeof(int)),
            ("Enabled", typeof(bool)),
            ("InvalidPercent", typeof(double)),
            ("MinerEffortPercent", typeof(double?)),
            ("MinerEffortTime", typeof(int?)),
            ("Time", typeof(int)),
        }, properties);
    }

    [Fact]
    public void PoolResponses_PreserveBanningPropertyNamesAndValues()
    {
        var banning = new ApiPoolShareBasedBanningConfig
        {
            Enabled = true,
            CheckThreshold = 25,
            InvalidPercent = 12.5,
            Time = 600,
            MinerEffortPercent = 250.25,
            MinerEffortTime = 900,
        };
        var options = CreateApiJsonOptions(false);

        var pools = JsonSerializer.SerializeToElement(new GetPoolsResponse
        {
            Pools = new[] { new PoolInfo { ShareBasedBanning = banning } },
        }, options);
        var pool = JsonSerializer.SerializeToElement(new GetPoolResponse
        {
            Pool = new PoolInfo { ShareBasedBanning = banning },
        }, options);

        AssertBanningContract(
            pools.GetProperty("pools")[0].GetProperty("shareBasedBanning"));
        AssertBanningContract(
            pool.GetProperty("pool").GetProperty("shareBasedBanning"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolResponses_PreserveNullableBanningFields(bool legacyNulls)
    {
        var banning = new ApiPoolShareBasedBanningConfig
        {
            Enabled = true,
            CheckThreshold = 25,
            InvalidPercent = 12.5,
            Time = 600,
        };
        var options = CreateApiJsonOptions(legacyNulls);
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new GetPoolsResponse
            {
                Pools = new[]
                {
                    new PoolInfo { ShareBasedBanning = banning },
                },
            }, options).GetProperty("pools")[0],
            JsonSerializer.SerializeToElement(new GetPoolResponse
            {
                Pool = new PoolInfo { ShareBasedBanning = banning },
            }, options).GetProperty("pool"),
        };

        foreach(var payload in payloads)
        {
            var publicBanning = payload.GetProperty("shareBasedBanning");

            Assert.Equal(legacyNulls,
                publicBanning.TryGetProperty("minerEffortPercent",
                    out var effortPercent));
            Assert.Equal(legacyNulls,
                publicBanning.TryGetProperty("minerEffortTime",
                    out var effortTime));

            if(legacyNulls)
            {
                Assert.Equal(JsonValueKind.Null, effortPercent.ValueKind);
                Assert.Equal(JsonValueKind.Null, effortTime.ValueKind);
            }

            Assert.True(publicBanning.GetProperty("enabled").GetBoolean());
            Assert.Equal(25,
                publicBanning.GetProperty("checkThreshold").GetInt32());
            Assert.Equal(12.5,
                publicBanning.GetProperty("invalidPercent").GetDouble());
            Assert.Equal(600,
                publicBanning.GetProperty("time").GetInt32());
            Assert.Equal(legacyNulls ? 6 : 4,
                publicBanning.EnumerateObject().Count());
        }
    }

    public static IEnumerable<object[]> SensitivePaymentExtraProperties()
    {
        yield return new object[]
        {
            CoinFamily.Alephium,
            typeof(AlephiumPaymentProcessingConfigExtra),
            nameof(AlephiumPaymentProcessingConfigExtra.WalletPassword),
        };
        yield return new object[]
        {
            CoinFamily.Bitcoin,
            typeof(BitcoinPoolPaymentProcessingConfigExtra),
            nameof(BitcoinPoolPaymentProcessingConfigExtra.WalletPassword),
        };
        yield return new object[]
        {
            CoinFamily.Ergo,
            typeof(ErgoPaymentProcessingConfigExtra),
            nameof(ErgoPaymentProcessingConfigExtra.WalletPassword),
        };
        yield return new object[]
        {
            CoinFamily.Handshake,
            typeof(HandshakePoolPaymentProcessingConfigExtra),
            nameof(HandshakePoolPaymentProcessingConfigExtra.WalletPassword),
        };
        yield return new object[]
        {
            CoinFamily.Kaspa,
            typeof(KaspaPaymentProcessingConfigExtra),
            nameof(KaspaPaymentProcessingConfigExtra.WalletPassword),
        };
        yield return new object[]
        {
            CoinFamily.Warthog,
            typeof(WarthogPaymentProcessingConfigExtra),
            nameof(WarthogPaymentProcessingConfigExtra.WalletPrivateKey),
        };
    }

    [Fact]
    public void SensitivePaymentExtraInventory_IsCompletelyCovered()
    {
        var expected = SensitivePaymentExtraProperties()
            .Select(values => $"{((Type) values[1]).FullName}.{values[2]}")
            .OrderBy(value => value)
            .ToArray();
        var candidates = typeof(PoolConfig).Assembly.GetExportedTypes()
            .Where(IsPaymentConfigurationType)
            .SelectMany(type => type.GetProperties()
                .Where(property => IsSensitivePropertyName(property.Name))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .OrderBy(value => value)
            .ToArray();
        var actual = candidates
            .Except(KnownBenignSensitivePaymentPropertyNames,
                StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.All(KnownBenignSensitivePaymentPropertyNames,
            member => Assert.Contains(member, candidates));
    }

    [Theory]
    [MemberData(nameof(SensitivePaymentExtraProperties))]
    public void ToPoolInfo_StripsEverySensitivePaymentExtra(
        CoinFamily family, Type extraType, string sensitiveProperty)
    {
        const string retainedProperty = "publicSetting";
        Assert.NotNull(extraType.GetProperty(sensitiveProperty));

        var spellings = new[]
        {
            sensitiveProperty,
            JsonNamingPolicy.CamelCase.ConvertName(sensitiveProperty),
        };

        foreach(var serializedSensitiveProperty in spellings.Distinct(
                    StringComparer.Ordinal))
        {
            var config = CreateMinimalPoolConfig(family);
            config.PaymentProcessing.Extra = new Dictionary<string, object>
            {
                [serializedSensitiveProperty] = "secret-value",
                [retainedProperty] = "public-value",
            };

            var result = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
                new global::Miningcore.Persistence.Model.PoolStats(), null);

            Assert.False(result.PaymentProcessing.Extra.ContainsKey(
                serializedSensitiveProperty));
            Assert.Equal("public-value",
                result.PaymentProcessing.Extra[retainedProperty]);
            Assert.Equal("secret-value",
                config.PaymentProcessing.Extra[serializedSensitiveProperty]);
        }
    }

    [Fact]
    public void ToPoolInfo_UsesDedicatedEndpointDtosAndOmitsPrivateListenerData()
    {
        var sourceEndpoint = new PoolEndpoint
        {
            ListenAddress = "127.0.0.1",
            Name = "public-endpoint",
            Difficulty = 42,
            VarDiff = new VarDiffConfig
            {
                MinDiff = 1,
                MaxDiff = 100,
                MaxDelta = 10,
                TargetTime = 15,
                RetargetTime = 90,
                VariancePercent = 30,
            },
            TcpProxyProtocol = new TcpProxyProtocolConfig
            {
                Enable = true,
                Mandatory = true,
                ProxyAddresses = new[] { "10.0.0.5" },
            },
            Tls = true,
            TlsAuto = true,
            TlsPfxFile = "pool.pfx",
            TlsPfxPassword = "secret",
        };
        var config = CreateMinimalPoolConfig();
        config.Ports = new Dictionary<int, PoolEndpoint>
        {
            [3031] = sourceEndpoint,
            [3032] = null,
        };
        var mapper = AutoMapperFactory.CreateMapper();

        var result = config.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        var endpoint = Assert.IsType<ApiPoolEndpoint>(
            Assert.Single(result.Ports).Value);
        Assert.Equal(sourceEndpoint.ListenAddress, endpoint.ListenAddress);
        Assert.Equal(sourceEndpoint.Name, endpoint.Name);
        Assert.Equal(sourceEndpoint.Difficulty, endpoint.Difficulty);
        Assert.Equal(sourceEndpoint.VarDiff.MinDiff, endpoint.VarDiff.MinDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MaxDiff, endpoint.VarDiff.MaxDiff);
        Assert.Equal(sourceEndpoint.VarDiff.MaxDelta, endpoint.VarDiff.MaxDelta);
        Assert.Equal(sourceEndpoint.VarDiff.TargetTime,
            endpoint.VarDiff.TargetTime);
        Assert.Equal(sourceEndpoint.VarDiff.RetargetTime,
            endpoint.VarDiff.RetargetTime);
        Assert.Equal(sourceEndpoint.VarDiff.VariancePercent,
            endpoint.VarDiff.VariancePercent);
        Assert.True(endpoint.TcpProxyProtocol.Enable);
        Assert.True(endpoint.TcpProxyProtocol.Mandatory);
        Assert.True(endpoint.Tls);
        Assert.True(endpoint.TlsAuto);

        var json = JsonSerializer.Serialize(endpoint,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        using var document = JsonDocument.Parse(json);
        var publicEndpoint = document.RootElement;
        Assert.False(publicEndpoint.TryGetProperty("tlsPfxFile", out _));
        Assert.False(publicEndpoint.TryGetProperty("tlsPfxPassword", out _));
        Assert.True(publicEndpoint.TryGetProperty("tcpProxyProtocol",
            out var publicProxyProtocol));
        Assert.False(publicProxyProtocol.TryGetProperty("proxyAddresses",
            out _));

        Assert.False(result.Ports.ContainsKey(3032));
        Assert.Equal(2, config.Ports.Count);
        Assert.Null(config.Ports[3032]);
        Assert.Equal("pool.pfx", sourceEndpoint.TlsPfxFile);
        Assert.Equal("secret", sourceEndpoint.TlsPfxPassword);
        Assert.Equal(new[] { "10.0.0.5" },
            sourceEndpoint.TcpProxyProtocol.ProxyAddresses);
    }

    [Fact]
    public void ConfigurePayoutSchemeConfig_WithSoloAndNoSchemeConfig_IsNullSafe()
    {
        var poolInfo = new PoolInfo
        {
            PaymentProcessing = new ApiPoolPaymentProcessingConfig()
        };
        var payoutConfig = new PoolPaymentProcessingConfig
        {
            Enabled = true,
            PayoutScheme = PayoutScheme.SOLO
        };

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo, payoutConfig);

        Assert.NotNull(poolInfo.PaymentProcessing.PayoutSchemeConfig);
        Assert.Null(poolInfo.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage);
    }

    [Fact]
    public void ConfigurePayoutSchemeConfig_WithMissingMappedPaymentConfig_IsNullSafe()
    {
        var poolInfo = new PoolInfo();

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo, null);

        Assert.NotNull(poolInfo.PaymentProcessing);
        Assert.NotNull(poolInfo.PaymentProcessing.PayoutSchemeConfig);
        Assert.Null(poolInfo.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage);
    }

    [Fact]
    public void ShouldCalculatePoolEffort_WithLastBlockButNoRuntimePool_ReturnsFalse()
    {
        Assert.False(PoolApiController.ShouldCalculatePoolEffort(DateTime.UtcNow, null));
    }

    [Fact]
    public void ShouldCalculatePoolEffort_WithRuntimePoolButNoLastBlock_ReturnsFalse()
    {
        Assert.False(PoolApiController.ShouldCalculatePoolEffort(null,
            Substitute.For<IMiningPool>()));
    }

    [Fact]
    public void ShouldCalculatePoolEffort_WithRuntimePoolAndLastBlock_ReturnsTrue()
    {
        Assert.True(PoolApiController.ShouldCalculatePoolEffort(DateTime.UtcNow,
            Substitute.For<IMiningPool>()));
    }

    private static PoolConfig CreateMinimalPoolConfig(
        CoinFamily family = CoinFamily.Alephium)
    {
        return new PoolConfig
        {
            // Alephium's algorithm name is constant, so this test fixture does
            // not need a configured hasher graph. ToPoolInfo derives the public
            // family from Family rather than the template CLR type, allowing
            // this safe template to exercise every redaction branch.
            Template = new AlephiumCoinTemplate
            {
                Family = family,
                Name = family.ToString(),
                Symbol = family.ToString().ToUpperInvariant(),
            },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
        };
    }

    private static JsonSerializerOptions CreateApiJsonOptions(bool legacyNulls)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Program.ConfigureApiJsonSerializerOptions(options, legacyNulls);
        return options;
    }

    // This inventory intentionally targets configuration types that can feed
    // PaymentProcessing.Extra. If a future unrelated type matches these naming
    // heuristics, narrow this type predicate; do not suppress its credential
    // properties through the reviewed benign-property allow-list.
    private static bool IsPaymentConfigurationType(Type type) =>
        type.Namespace?.StartsWith("Miningcore.Blockchain.",
            StringComparison.Ordinal) == true &&
        type.Name.Contains("Payment", StringComparison.OrdinalIgnoreCase) &&
        type.Name.Contains("Config", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitivePropertyName(string name) =>
        name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mnemonic", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Seed", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Key", StringComparison.OrdinalIgnoreCase);

    private static void AssertBanningContract(JsonElement banning)
    {
        Assert.Equal(new[]
        {
            "checkThreshold",
            "enabled",
            "invalidPercent",
            "minerEffortPercent",
            "minerEffortTime",
            "time",
        }, banning.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray());
        Assert.True(banning.GetProperty("enabled").GetBoolean());
        Assert.Equal(25, banning.GetProperty("checkThreshold").GetInt32());
        Assert.Equal(12.5,
            banning.GetProperty("invalidPercent").GetDouble());
        Assert.Equal(600, banning.GetProperty("time").GetInt32());
        Assert.Equal(250.25,
            banning.GetProperty("minerEffortPercent").GetDouble());
        Assert.Equal(900,
            banning.GetProperty("minerEffortTime").GetInt32());
    }
}
