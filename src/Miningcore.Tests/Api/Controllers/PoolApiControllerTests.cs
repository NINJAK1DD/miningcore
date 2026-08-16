using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Miningcore.Api.Controllers;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Conceal.Configuration;
using Miningcore.Blockchain.Cryptonote.Configuration;
using Miningcore.Blockchain.Ergo.Configuration;
using Miningcore.Blockchain.Ethereum.Configuration;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Blockchain.Xelis.Configuration;
using Miningcore.Blockchain.Zano.Configuration;
using Miningcore.Configuration;
using Miningcore.Mining;
using NSubstitute;
using Newtonsoft.Json.Linq;
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

    private static readonly PaymentExtraContract[] PaymentExtraContracts =
    {
        Contract(CoinFamily.Alephium,
            typeof(AlephiumPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(AlephiumPaymentProcessingConfigExtra.WalletName)] =
                    "wallet-name",
                [nameof(AlephiumPaymentProcessingConfigExtra.
                    BlockRewardsLockTime)] = 123L,
                [nameof(AlephiumPaymentProcessingConfigExtra.
                    KeepTransactionFees)] = true,
            }, nameof(AlephiumPaymentProcessingConfigExtra.WalletPassword)),
        Contract(CoinFamily.Beam, null),
        BitcoinContract(CoinFamily.Bitcoin),
        Contract(CoinFamily.Conceal,
            typeof(ConcealPoolPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(ConcealPoolPaymentProcessingConfigExtra.
                    MinimumPaymentToPaymentId)] = 1.25m,
            }),
        Contract(CoinFamily.Cryptonote,
            typeof(CryptonotePoolPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(CryptonotePoolPaymentProcessingConfigExtra.
                    MinimumPaymentToPaymentId)] = 2.5m,
                [nameof(CryptonotePoolPaymentProcessingConfigExtra.
                    MaximumDestinationPerTransfer)] = 16,
            }),
        BitcoinContract(CoinFamily.Equihash),
        Contract(CoinFamily.Ergo,
            typeof(ErgoPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(ErgoPaymentProcessingConfigExtra.
                    MinimumConfirmations)] = 720,
            }, nameof(ErgoPaymentProcessingConfigExtra.WalletPassword)),
        Contract(CoinFamily.Ethereum,
            typeof(EthereumPoolPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(EthereumPoolPaymentProcessingConfigExtra.
                    KeepTransactionFees)] = true,
                [nameof(EthereumPoolPaymentProcessingConfigExtra.KeepUncles)] =
                    false,
                [nameof(EthereumPoolPaymentProcessingConfigExtra.Gas)] =
                    21000UL,
                [nameof(EthereumPoolPaymentProcessingConfigExtra.
                    MaxFeePerGas)] = 3000000000UL,
                [nameof(EthereumPoolPaymentProcessingConfigExtra.
                    BlockSearchOffset)] = 50U,
            }),
        Contract(CoinFamily.Handshake,
            typeof(HandshakePoolPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(HandshakePoolPaymentProcessingConfigExtra.WalletName)] =
                    "primary",
                [nameof(HandshakePoolPaymentProcessingConfigExtra.
                    WalletAccount)] = "default",
                [nameof(HandshakePoolPaymentProcessingConfigExtra.
                    MinersPayTxFees)] = true,
            }, nameof(HandshakePoolPaymentProcessingConfigExtra.
                WalletPassword)),
        Contract(CoinFamily.Kaspa,
            typeof(KaspaPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(KaspaPaymentProcessingConfigExtra.
                    MinimumConfirmations)] = 120,
                [nameof(KaspaPaymentProcessingConfigExtra.
                    VersionEnablingMaxFee)] = "v0.12.18-rc5",
                [nameof(KaspaPaymentProcessingConfigExtra.MaxFee)] = 20000UL,
            }, nameof(KaspaPaymentProcessingConfigExtra.WalletPassword)),
        BitcoinContract(CoinFamily.Nexa),
        BitcoinContract(CoinFamily.Progpow),
        BitcoinContract(CoinFamily.Satoshicash),
        Contract(CoinFamily.Warthog,
            typeof(WarthogPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(WarthogPaymentProcessingConfigExtra.
                    MaximumTransactionFees)] = 1.5m,
                [nameof(WarthogPaymentProcessingConfigExtra.
                    KeepTransactionFees)] = true,
                [nameof(WarthogPaymentProcessingConfigExtra.
                    MinimumConfirmations)] = 120,
                [nameof(WarthogPaymentProcessingConfigExtra.
                    MaxDegreeOfParallelPayouts)] = 2,
            }, nameof(WarthogPaymentProcessingConfigExtra.WalletPrivateKey)),
        Contract(CoinFamily.Xelis,
            typeof(XelisPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(XelisPaymentProcessingConfigExtra.
                    MinimumConfirmations)] = 60,
                [nameof(XelisPaymentProcessingConfigExtra.
                    MaximumDestinationPerTransfer)] = 255,
                [nameof(XelisPaymentProcessingConfigExtra.
                    KeepTransactionFees)] = true,
            }),
        Contract(CoinFamily.Zano,
            typeof(ZanoPoolPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(ZanoPoolPaymentProcessingConfigExtra.
                    MinimumPaymentToPaymentId)] = 3.75m,
                [nameof(ZanoPoolPaymentProcessingConfigExtra.
                    RevealPoolAddress)] = true,
                [nameof(ZanoPoolPaymentProcessingConfigExtra.
                    HideMinerAddress)] = false,
                [nameof(ZanoPoolPaymentProcessingConfigExtra.
                    MaximumDestinationPerTransfer)] = 256,
                [nameof(ZanoPoolPaymentProcessingConfigExtra.
                    KeepTransactionFees)] = true,
                [nameof(ZanoPoolPaymentProcessingConfigExtra.MaxFee)] =
                    10000000000UL,
            }),
    };

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
    public void PoolResponses_PreserveNestedPaymentProcessingExtraContract(
        bool legacyNulls)
    {
        const string retainedProperty = "MinersPayTxFees";
        var config = CreateMinimalPoolConfig(CoinFamily.Bitcoin);
        config.PaymentProcessing.Extra = new Dictionary<string, object>
        {
            [retainedProperty] = true,
            [nameof(BitcoinPoolPaymentProcessingConfigExtra.WalletPassword)] =
                "secret-value",
        };
        var poolInfo = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var options = CreateApiJsonOptions(legacyNulls);
        var payloads = new[]
        {
            JsonSerializer.SerializeToElement(new GetPoolsResponse
            {
                Pools = new[]
                {
                    poolInfo,
                },
            }, options).GetProperty("pools")[0],
            JsonSerializer.SerializeToElement(new GetPoolResponse
            {
                Pool = poolInfo,
            }, options).GetProperty("pool"),
        };

        foreach(var payload in payloads)
        {
            var publicPaymentProcessing =
                payload.GetProperty("paymentProcessing");

            Assert.False(publicPaymentProcessing.TryGetProperty(
                retainedProperty, out _));
            Assert.True(publicPaymentProcessing
                .GetProperty("extra")
                .GetProperty(retainedProperty)
                .GetBoolean());
            Assert.False(publicPaymentProcessing.GetProperty("extra")
                .EnumerateObject().Any(property => property.Name.Equals(
                    nameof(BitcoinPoolPaymentProcessingConfigExtra.
                        WalletPassword),
                    StringComparison.OrdinalIgnoreCase)));
        }
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

    public static IEnumerable<object[]> PaymentExtraFamilyCases()
    {
        foreach(var contract in PaymentExtraContracts)
        {
            yield return new object[] { contract, false };
            yield return new object[] { contract, true };
        }
    }

    [Fact]
    public void PaymentExtraInventory_ClassifiesEveryFamilyTypeAndProperty()
    {
        var discoveredTypes = typeof(PoolConfig).Assembly.GetExportedTypes()
            .Where(IsPaymentConfigurationType)
            .OrderBy(type => type.FullName)
            .ToArray();
        var classifiedTypes = PaymentExtraContracts
            .Select(contract => contract.RuntimeType)
            .Where(type => type != null)
            .Distinct()
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.Equal(discoveredTypes, classifiedTypes);
        Assert.Equal(Enum.GetValues<CoinFamily>().OrderBy(value => value),
            PaymentExtraContracts.Select(contract => contract.Family)
                .OrderBy(value => value));

        foreach(var runtimeType in discoveredTypes)
        {
            var contracts = PaymentExtraContracts
                .Where(contract => contract.RuntimeType == runtimeType)
                .ToArray();
            var classifiedNames = contracts
                .SelectMany(contract => contract.PublicValues.Keys.Concat(
                    contract.SensitiveProperties))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value)
                .ToArray();
            var runtimeNames = runtimeType.GetProperties()
                .Select(property => property.Name)
                .OrderBy(value => value)
                .ToArray();

            Assert.Equal(runtimeNames, classifiedNames);
        }

        var sensitiveMembers = discoveredTypes
            .SelectMany(type => type.GetProperties()
                .Where(property => IsSensitivePropertyName(property.Name))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .OrderBy(value => value)
            .ToArray();
        var unreviewedSensitiveMembers = sensitiveMembers
            .Except(KnownBenignSensitivePaymentPropertyNames,
                StringComparer.Ordinal)
            .ToArray();
        var classifiedSensitiveMembers = PaymentExtraContracts
            .Where(contract => contract.RuntimeType != null)
            .SelectMany(contract => contract.SensitiveProperties.Select(
                property => $"{contract.RuntimeType.FullName}.{property}"))
            .Distinct(StringComparer.Ordinal)
            .Except(KnownBenignSensitivePaymentPropertyNames,
                StringComparer.Ordinal)
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(unreviewedSensitiveMembers,
            classifiedSensitiveMembers);
        Assert.All(KnownBenignSensitivePaymentPropertyNames,
            member => Assert.Contains(member, sensitiveMembers));
    }

    [Fact]
    public void PaymentExtraDto_ExposesOnlyTheApprovedTypedUnion()
    {
        var properties = typeof(ApiPoolPaymentProcessingExtra)
            .GetProperties()
            .OrderBy(property => property.Name)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.Equal(new[]
        {
            ("BlockRewardsLockTime", typeof(long?)),
            ("BlockSearchOffset", typeof(uint?)),
            ("Gas", typeof(ulong?)),
            ("HideMinerAddress", typeof(bool?)),
            ("KeepTransactionFees", typeof(bool?)),
            ("KeepUncles", typeof(bool?)),
            ("MaxDegreeOfParallelPayouts", typeof(int?)),
            ("MaxFee", typeof(ulong?)),
            ("MaxFeePerGas", typeof(ulong?)),
            ("MaximumDestinationPerTransfer", typeof(int?)),
            ("MaximumTransactionFees", typeof(decimal?)),
            ("MinersPayTxFees", typeof(bool?)),
            ("MinimumConfirmations", typeof(int?)),
            ("MinimumPaymentToPaymentId", typeof(decimal?)),
            ("RevealPoolAddress", typeof(bool?)),
            ("VersionEnablingMaxFee", typeof(string)),
            ("WalletAccount", typeof(string)),
            ("WalletName", typeof(string)),
        }, properties);

        var sensitiveNames = PaymentExtraContracts
            .SelectMany(contract => contract.SensitiveProperties)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(properties,
            property => sensitiveNames.Contains(property.Name,
                StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(PaymentExtraFamilyCases))]
    public void ToPoolInfo_ProjectsOnlyApprovedPaymentExtraForEveryFamily(
        PaymentExtraContract contract, bool legacyNulls)
    {
        const string unknownProperty = "FutureInternalSetting";
        var source = new Dictionary<string, object>(contract.PublicValues,
            StringComparer.Ordinal)
        {
            [unknownProperty] = "must-not-be-public",
        };

        foreach(var sensitiveProperty in contract.SensitiveProperties)
        {
            source[sensitiveProperty] = "secret-value";
            source[JsonNamingPolicy.CamelCase.ConvertName(sensitiveProperty)] =
                "second-secret-value";
        }

        var sourceSnapshot = source.ToDictionary(pair => pair.Key,
            pair => pair.Value, StringComparer.Ordinal);
        var config = CreateMinimalPoolConfig(contract.Family);
        config.PaymentProcessing.Extra = source;
        var result = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        Assert.IsType<ApiPoolPaymentProcessingExtra>(
            result.PaymentProcessing.Extra);
        Assert.NotSame(source, result.PaymentProcessing.Extra);
        Assert.Equal(sourceSnapshot.Count, source.Count);

        foreach(var expected in sourceSnapshot)
            Assert.Equal(expected.Value, source[expected.Key]);

        if(contract.PublicValues.Count > 0)
        {
            source[contract.PublicValues.Keys.First()] =
                "mutated-after-projection";
        }

        var options = CreateApiJsonOptions(legacyNulls);
        var payloads = SerializePoolResponsePayloads(result, options);

        foreach(var payload in payloads)
        {
            var payment = payload.GetProperty("paymentProcessing");
            var extra = payment.GetProperty("extra");
            var actualNames = extra.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(value => value)
                .ToArray();
            var expectedNames = contract.PublicValues.Keys
                .OrderBy(value => value)
                .ToArray();

            Assert.Equal(expectedNames, actualNames);
            Assert.DoesNotContain(payment.EnumerateObject(), property =>
                expectedNames.Contains(property.Name, StringComparer.Ordinal));
            Assert.False(extra.TryGetProperty(unknownProperty, out _));

            foreach(var expected in contract.PublicValues)
            {
                var expectedJson = JsonSerializer.SerializeToElement(
                    expected.Value, options);
                Assert.True(JsonElement.DeepEquals(expectedJson,
                    extra.GetProperty(expected.Key)));
            }
        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaymentExtraProjection_PreservesExplicitNullAndKeyCasing(
        bool legacyNulls)
    {
        const string configuredName = "walletName";
        var config = CreateMinimalPoolConfig(CoinFamily.Alephium);
        config.PaymentProcessing.Extra = new Dictionary<string, object>
        {
            [configuredName] = null,
        };
        var result = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var payloads = SerializePoolResponsePayloads(result,
            CreateApiJsonOptions(legacyNulls));

        foreach(var payload in payloads)
        {
            var extra = payload.GetProperty("paymentProcessing")
                .GetProperty("extra");

            Assert.Equal(new[] { configuredName }, extra.EnumerateObject()
                .Select(property => property.Name).ToArray());
            Assert.Equal(JsonValueKind.Null,
                extra.GetProperty(configuredName).ValueKind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaymentExtraProjection_PreservesNullAndEmptyContainerContract(
        bool legacyNulls)
    {
        var nullConfig = CreateMinimalPoolConfig(CoinFamily.Bitcoin);
        nullConfig.PaymentProcessing.Extra = null;
        var emptyConfig = CreateMinimalPoolConfig(CoinFamily.Bitcoin);
        emptyConfig.PaymentProcessing.Extra =
            new Dictionary<string, object>();
        var mapper = AutoMapperFactory.CreateMapper();
        var options = CreateApiJsonOptions(legacyNulls);

        var nullPayloads = SerializePoolResponsePayloads(
            nullConfig.ToPoolInfo(mapper,
                new global::Miningcore.Persistence.Model.PoolStats(), null),
            options);
        var emptyPayloads = SerializePoolResponsePayloads(
            emptyConfig.ToPoolInfo(mapper,
                new global::Miningcore.Persistence.Model.PoolStats(), null),
            options);

        for(var i = 0; i < nullPayloads.Length; i++)
        {
            var nullPayment = nullPayloads[i]
                .GetProperty("paymentProcessing");
            var emptyExtra = emptyPayloads[i]
                .GetProperty("paymentProcessing")
                .GetProperty("extra");

            Assert.Equal(legacyNulls,
                nullPayment.TryGetProperty("extra", out var nullExtra));

            if(legacyNulls)
                Assert.Equal(JsonValueKind.Null, nullExtra.ValueKind);

            Assert.Empty(emptyExtra.EnumerateObject());
        }
    }

    [Fact]
    public void PaymentExtraProjection_FailsClosedPerAmbiguousOrMalformedField()
    {
        var duplicateConfig = CreateMinimalPoolConfig(CoinFamily.Bitcoin);
        duplicateConfig.PaymentProcessing.Extra =
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["MinersPayTxFees"] = true,
                ["minersPayTxFees"] = false,
            };
        var malformedConfig = CreateMinimalPoolConfig(CoinFamily.Ethereum);
        malformedConfig.PaymentProcessing.Extra =
            new Dictionary<string, object>
            {
                ["Gas"] = "not-an-integer",
                ["KeepUncles"] = true,
            };
        var mapper = AutoMapperFactory.CreateMapper();

        var duplicate = duplicateConfig.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var malformed = malformedConfig.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var options = CreateApiJsonOptions(false);
        var duplicateExtra = JsonSerializer.SerializeToElement(
            duplicate.PaymentProcessing.Extra, options);
        var malformedExtra = JsonSerializer.SerializeToElement(
            malformed.PaymentProcessing.Extra, options);

        Assert.Empty(duplicateExtra.EnumerateObject());
        Assert.Equal(new[] { "KeepUncles" }, malformedExtra
            .EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(malformedExtra.GetProperty("KeepUncles").GetBoolean());
    }

    [Fact]
    public void PaymentExtraSerializers_PreserveNestedTypedShapeAndRoundTrip()
    {
        var config = CreateMinimalPoolConfig(CoinFamily.Bitcoin);
        config.PaymentProcessing.Extra = new Dictionary<string, object>
        {
            ["MinersPayTxFees"] = true,
        };
        var payment = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null)
            .PaymentProcessing;
        var systemTextOptions = CreateApiJsonOptions(false);

        var systemTextJson = JsonSerializer.Serialize(payment,
            systemTextOptions);
        var systemTextRoundTrip = JsonSerializer.Deserialize<
            ApiPoolPaymentProcessingConfig>(systemTextJson,
            systemTextOptions);
        var defaultNewtonsoftJson = Newtonsoft.Json.JsonConvert.
            SerializeObject(payment);
        var newtonsoftJson = Newtonsoft.Json.JsonConvert.SerializeObject(
            payment, new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.
                    CamelCasePropertyNamesContractResolver(),
            });
        var newtonsoftRoundTrip = Newtonsoft.Json.JsonConvert.
            DeserializeObject<ApiPoolPaymentProcessingConfig>(newtonsoftJson);

        foreach(var json in new[]
                {
                    systemTextJson,
                    JsonSerializer.Serialize(systemTextRoundTrip,
                        systemTextOptions),
                    newtonsoftJson,
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        newtonsoftRoundTrip, new Newtonsoft.Json.
                            JsonSerializerSettings
                            {
                                ContractResolver = new Newtonsoft.Json.
                                    Serialization.
                                    CamelCasePropertyNamesContractResolver(),
                            }),
                })
        {
            var payload = JObject.Parse(json);
            Assert.Null(payload["MinersPayTxFees"]);
            Assert.True(payload["extra"]?["MinersPayTxFees"]?.Value<bool>());
        }

        var defaultNewtonsoftPayload = JObject.Parse(defaultNewtonsoftJson);
        Assert.Null(defaultNewtonsoftPayload["MinersPayTxFees"]);
        Assert.True(defaultNewtonsoftPayload["Extra"]?["MinersPayTxFees"]?
            .Value<bool>());
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

    private static PaymentExtraContract BitcoinContract(CoinFamily family) =>
        Contract(family, typeof(BitcoinPoolPaymentProcessingConfigExtra),
            new Dictionary<string, object>
            {
                [nameof(BitcoinPoolPaymentProcessingConfigExtra.
                    MinersPayTxFees)] = true,
            }, nameof(BitcoinPoolPaymentProcessingConfigExtra.WalletPassword));

    private static PaymentExtraContract Contract(CoinFamily family,
        Type runtimeType, Dictionary<string, object> publicValues = null,
        params string[] sensitiveProperties) =>
        new(family, runtimeType,
            publicValues ?? new Dictionary<string, object>(),
            sensitiveProperties ?? Array.Empty<string>());

    private static JsonElement[] SerializePoolResponsePayloads(
        PoolInfo poolInfo, JsonSerializerOptions options)
    {
        return new[]
        {
            JsonSerializer.SerializeToElement(new GetPoolsResponse
            {
                Pools = new[] { poolInfo },
            }, options).GetProperty("pools")[0],
            JsonSerializer.SerializeToElement(new GetPoolResponse
            {
                Pool = poolInfo,
            }, options).GetProperty("pool"),
        };
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

    // This inventory deliberately uses broader naming heuristics than the
    // current *PaymentProcessingConfigExtra convention. A newly introduced
    // blockchain payment configuration must therefore be reviewed and added
    // to the explicit public/sensitive contract instead of silently escaping
    // the fail-closed projection tests under a different class name.
    private static bool IsPaymentConfigurationType(Type type) =>
        type.Namespace?.StartsWith("Miningcore.Blockchain.",
            StringComparison.Ordinal) == true &&
        type.Name.Contains("Payment", StringComparison.OrdinalIgnoreCase) &&
        type.Name.Contains("Config", StringComparison.OrdinalIgnoreCase);

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

    public sealed record PaymentExtraContract(CoinFamily Family,
        Type RuntimeType, Dictionary<string, object> PublicValues,
        string[] SensitiveProperties)
    {
        public override string ToString() => Family.ToString();
    }
}
