using System;
using System.Collections.Generic;
using System.IO;
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
            foreach(var sourceKind in Enum.GetValues<PaymentExtraSourceKind>())
            {
                yield return new object[] { contract, false, sourceKind };
                yield return new object[] { contract, true, sourceKind };
            }
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
        PaymentExtraContract contract, bool legacyNulls,
        PaymentExtraSourceKind sourceKind)
    {
        const string unknownProperty = "FutureInternalSetting";
        var source = contract.PublicValues.ToDictionary(pair => pair.Key,
            pair => sourceKind == PaymentExtraSourceKind.DefensiveJToken ?
                (object) ToWireToken(pair.Value) :
                pair.Value, StringComparer.Ordinal);
        source.Add(unknownProperty,
            sourceKind == PaymentExtraSourceKind.DefensiveJToken ?
                new JValue("must-not-be-public") :
                "must-not-be-public");

        foreach(var sensitiveProperty in contract.SensitiveProperties)
        {
            source[sensitiveProperty] = sourceKind ==
                PaymentExtraSourceKind.DefensiveJToken ?
                new JValue("secret-value") : "secret-value";
            source[JsonNamingPolicy.CamelCase.ConvertName(sensitiveProperty)] =
                sourceKind == PaymentExtraSourceKind.DefensiveJToken ?
                    new JValue("second-secret-value") :
                    "second-secret-value";
        }

        if(sourceKind == PaymentExtraSourceKind.NewtonsoftConfiguration)
        {
            var runtimeJson = JObject.FromObject(source);
            source = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    PoolPaymentProcessingConfig>(runtimeJson.ToString(
                    Newtonsoft.Json.Formatting.None)).Extra
                .ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal);
        }

        var sourceSnapshot = source.ToDictionary(pair => pair.Key,
            pair => ToWireToken(pair.Value).DeepClone(),
            StringComparer.Ordinal);
        var config = CreateMinimalPoolConfig(contract.Family);
        config.PaymentProcessing.Extra = source;
        var result = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        Assert.IsType<ApiPoolPaymentProcessingExtra>(
            result.PaymentProcessing.Extra);
        Assert.NotSame(source, result.PaymentProcessing.Extra);
        Assert.Equal(sourceSnapshot.Count, source.Count);

        foreach(var expected in sourceSnapshot)
        {
            Assert.True(JToken.DeepEquals(expected.Value,
                ToWireToken(source[expected.Key])));
        }

        if(contract.PublicValues.Count > 0)
        {
            var firstKey = contract.PublicValues.Keys.First();

            if(source[firstKey] is JValue token)
                token.Value = "mutated-after-projection";
            else
                source[firstKey] = "mutated-after-projection";
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

    public static IEnumerable<object[]> CoerciblePaymentExtraCases()
    {
        var cases = new[]
        {
            new CoerciblePaymentExtraCase(CoinFamily.Ethereum,
                "gas", new JValue("21000"), 21000UL),
            new CoerciblePaymentExtraCase(CoinFamily.Ethereum,
                "keepUncles", new JValue(1), true),
            new CoerciblePaymentExtraCase(CoinFamily.Handshake,
                "walletName", new JValue(123), "123"),
            new CoerciblePaymentExtraCase(CoinFamily.Kaspa,
                "minimumConfirmations", new JValue("120"), 120),
            new CoerciblePaymentExtraCase(CoinFamily.Kaspa,
                "minimumConfirmations", new JValue(12.7), 13),
            new CoerciblePaymentExtraCase(CoinFamily.Handshake,
                "walletName", new JValue("2026-08-16T15:30:00Z"),
                "2026-08-16T15:30:00Z"),
            new CoerciblePaymentExtraCase(CoinFamily.Handshake,
                "walletAccount", new JValue("2026-08-16T15:31:00Z"),
                "2026-08-16T15:31:00Z"),
            new CoerciblePaymentExtraCase(CoinFamily.Kaspa,
                "versionEnablingMaxFee",
                new JValue("2026-08-16T15:32:00Z"),
                "2026-08-16T15:32:00Z"),
            new CoerciblePaymentExtraCase(CoinFamily.Handshake,
                "walletName", new JValue("2025-01-01"),
                "2025-01-01"),
            new CoerciblePaymentExtraCase(CoinFamily.Handshake,
                "walletAccount", new JValue("2025-01-01T00:00:00"),
                "2025-01-01T00:00:00"),
            new CoerciblePaymentExtraCase(CoinFamily.Handshake,
                "walletName", new JValue("2026-08-16T15:30:00+01:00"),
                "2026-08-16T15:30:00+01:00"),
        };

        foreach(var testCase in cases)
        {
            yield return new object[] { testCase, false };
            yield return new object[] { testCase, true };
        }
    }

    [Theory]
    [MemberData(nameof(CoerciblePaymentExtraCases))]
    public void PaymentExtraProjection_PreservesCoercibleConfiguredJsonTypes(
        CoerciblePaymentExtraCase testCase, bool legacyNulls)
    {
        var options = CreateApiJsonOptions(legacyNulls);
        var config = CreateMinimalPoolConfig(testCase.Family);
        var runtimeJson = new JObject
        {
            [testCase.Name] = testCase.WireValue.DeepClone(),
        };
        config.PaymentProcessing = Newtonsoft.Json.JsonConvert.
            DeserializeObject<PoolPaymentProcessingConfig>(
                runtimeJson.ToString(Newtonsoft.Json.Formatting.None),
                ConfigurationJson.CreateSerializerSettings());

        var sourceValue = config.PaymentProcessing.Extra[testCase.Name];
        Assert.False(sourceValue is JToken);

        if(testCase.WireValue.Type == JTokenType.String)
        {
            Assert.Equal(testCase.WireValue.Value<string>(),
                Assert.IsType<string>(sourceValue));
        }

        // This is the value the former object-valued response dictionary gave
        // System.Text.Json. Comparing against it pins before/after REST parity,
        // including date-looking configuration strings remaining strings.
        var expectedWire = JsonSerializer.SerializeToElement(sourceValue,
            options);
        var result = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        var actualClrValue = GetPaymentExtraClrValue(
            result.PaymentProcessing.Extra, testCase.Name);

        Assert.Equal(testCase.ExpectedClrValue, actualClrValue);

        config.PaymentProcessing.Extra[testCase.Name] =
            "mutated-after-projection";

        var systemTextJson = JsonSerializer.Serialize(
            result.PaymentProcessing.Extra, options);
        var systemTextRoundTrip = JsonSerializer.Deserialize<
            ApiPoolPaymentProcessingExtra>(systemTextJson, options);
        var newtonsoftJson = Newtonsoft.Json.JsonConvert.SerializeObject(
            result.PaymentProcessing.Extra);
        var newtonsoftRoundTrip = Newtonsoft.Json.JsonConvert.
            DeserializeObject<ApiPoolPaymentProcessingExtra>(newtonsoftJson);

        Assert.Equal(testCase.ExpectedClrValue, GetPaymentExtraClrValue(
            systemTextRoundTrip, testCase.Name));
        Assert.Equal(testCase.ExpectedClrValue, GetPaymentExtraClrValue(
            newtonsoftRoundTrip, testCase.Name));

        foreach(var json in new[]
                {
                    systemTextJson,
                    JsonSerializer.Serialize(systemTextRoundTrip, options),
                    newtonsoftJson,
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        newtonsoftRoundTrip),
                })
        {
            using var document = JsonDocument.Parse(json);
            Assert.True(JsonElement.DeepEquals(expectedWire,
                document.RootElement.GetProperty(testCase.Name)));
        }

        foreach(var payload in SerializePoolResponsePayloads(result, options))
        {
            var actual = payload.GetProperty("paymentProcessing")
                .GetProperty("extra").GetProperty(testCase.Name);

            Assert.True(JsonElement.DeepEquals(expectedWire, actual));
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
                ["MaxFeePerGas"] = new JObject
                {
                    ["unexpected"] = "object",
                },
                ["BlockSearchOffset"] = new JArray(1, 2),
                ["KeepUncles"] = true,
            };
        var dateConfig = CreateMinimalPoolConfig(CoinFamily.Handshake);
        dateConfig.PaymentProcessing.Extra = new Dictionary<string, object>
        {
            ["WalletName"] = new DateTime(2026, 8, 16, 15, 30, 0,
                DateTimeKind.Utc),
        };
        var mapper = AutoMapperFactory.CreateMapper();

        var duplicate = duplicateConfig.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var malformed = malformedConfig.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var date = dateConfig.ToPoolInfo(mapper,
            new global::Miningcore.Persistence.Model.PoolStats(), null);
        var options = CreateApiJsonOptions(false);
        var duplicateExtra = JsonSerializer.SerializeToElement(
            duplicate.PaymentProcessing.Extra, options);
        var malformedExtra = JsonSerializer.SerializeToElement(
            malformed.PaymentProcessing.Extra, options);
        var dateExtra = JsonSerializer.SerializeToElement(
            date.PaymentProcessing.Extra, options);

        Assert.Empty(duplicateExtra.EnumerateObject());
        Assert.Empty(dateExtra.EnumerateObject());
        Assert.Equal(new[] { "KeepUncles" }, malformedExtra
            .EnumerateObject().Select(property => property.Name).ToArray());
        Assert.True(malformedExtra.GetProperty("KeepUncles").GetBoolean());
    }

    [Fact]
    public void PaymentExtraProjection_PreservesConfiguredNameAtomically()
    {
        const string configuredName = "minimumConfirmations";
        var extra = new ApiPoolPaymentProcessingExtra();

        extra.SetMinimumConfirmations(configuredName, 120,
            new JValue("120"));

        Assert.Equal(120, extra.MinimumConfirmations);

        var payload = JsonSerializer.SerializeToElement(extra,
            CreateApiJsonOptions(false));
        var property = Assert.Single(payload.EnumerateObject());

        Assert.Equal(configuredName, property.Name);
        Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
        Assert.Equal("120", property.Value.GetString());
    }

    [Fact]
    public void PaymentExtraSystemTextJsonWriter_UsesConfiguredEncoderForStrings()
    {
        const string configuredValue = "</script>&é+'";
        var config = CreateMinimalPoolConfig(CoinFamily.Handshake);
        config.PaymentProcessing.Extra = new Dictionary<string, object>
        {
            ["WalletName"] = configuredValue,
        };
        var extra = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
                new global::Miningcore.Persistence.Model.PoolStats(), null)
            .PaymentProcessing.Extra;

        var json = JsonSerializer.Serialize(extra,
            CreateApiJsonOptions(false));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(configuredValue,
            document.RootElement.GetProperty("WalletName").GetString());
        Assert.Contains("\\u003C", json, StringComparison.Ordinal);
        Assert.Contains("\\u002B", json, StringComparison.Ordinal);
        Assert.Contains("\\u0027", json, StringComparison.Ordinal);
        Assert.DoesNotContain("</script>", json, StringComparison.Ordinal);
        Assert.DoesNotContain("&", json, StringComparison.Ordinal);
        Assert.DoesNotContain("é", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentExtraConverters_ReportMalformedApprovedValuesAsJsonErrors()
    {
        const string malformed = "{\"gas\":{\"unexpected\":true}}";

        Assert.Throws<System.Text.Json.JsonException>(() =>
            JsonSerializer.Deserialize<ApiPoolPaymentProcessingExtra>(
                malformed, CreateApiJsonOptions(false)));
        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<
                ApiPoolPaymentProcessingExtra>(malformed));
    }

    [Fact]
    public void PaymentExtraNewtonsoftReader_RestoresCallerDatePolicy()
    {
        const string configuredValue = "2026-08-16T15:30:00Z";
        var json = $$"""
            {
                "extra": {
                    "walletName": "{{configuredValue}}"
                },
                "following": "{{configuredValue}}"
            }
            """;

        var result = Newtonsoft.Json.JsonConvert.DeserializeObject<
            PaymentExtraNewtonsoftEnvelope>(json);

        Assert.Equal(configuredValue, result.Extra.WalletName);
        Assert.Equal(JTokenType.String, Assert.Single(
            result.Extra.PresentProperties).Value.WireValue.Type);
        Assert.Equal(JTokenType.Date, result.Following.Type);
    }

    [Fact]
    public void PaymentConfigNewtonsoftReader_ExplainsPreParsedDateTokens()
    {
        const string configuredValue = "2026-08-16T15:30:00+01:00";
        var source = JObject.Parse($$"""
            {
                "extra": {
                    "walletName": "{{configuredValue}}"
                }
            }
            """);

        Assert.Equal(JTokenType.Date,
            source["extra"]?["walletName"]?.Type);

        var error = Assert.Throws<Newtonsoft.Json.JsonSerializationException>(
            () => source.ToObject<ApiPoolPaymentProcessingConfig>());

        Assert.Contains("Payment property 'walletName' was parsed as a Date",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("DateParseHandling.None", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("cannot be recovered", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentConfigNewtonsoftReader_AcceptsPolicySafePreParsedStrings()
    {
        const string configuredValue = "2026-08-16T15:30:00+01:00";
        var json = $$"""
            {
                "extra": {
                    "walletName": "{{configuredValue}}"
                }
            }
            """;
        using var textReader = new StringReader(json);
        using var jsonReader = new Newtonsoft.Json.JsonTextReader(textReader)
        {
            DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
        };
        var source = JObject.Load(jsonReader);

        Assert.Equal(JTokenType.String,
            source["extra"]?["walletName"]?.Type);

        var result = source.ToObject<ApiPoolPaymentProcessingConfig>();

        Assert.Equal(configuredValue, result.Extra.WalletName);
        Assert.Equal(JTokenType.String,
            Assert.Single(result.Extra.PresentProperties).Value.WireValue.Type);
    }

    [Fact]
    public void PaymentExtraProjection_RequiresExplicitFamilyClassification()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PaymentProcessingExtraProjection.Create((CoinFamily) int.MaxValue,
                new Dictionary<string, object>()));
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
    public void ToPoolInfo_WithMissingPaymentProcessing_IsNullSafe()
    {
        var config = CreateMinimalPoolConfig();
        config.PaymentProcessing = null;

        var result = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        Assert.Null(result.PaymentProcessing);
        Assert.Null(config.PaymentProcessing);
        Assert.Equal(typeof(ApiPoolPaymentProcessingConfig),
            typeof(PoolInfo).GetProperty(nameof(PoolInfo.PaymentProcessing))?
                .PropertyType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolResponses_WithMissingPaymentProcessingOmitContract(
        bool legacyNulls)
    {
        var config = CreateMinimalPoolConfig();
        config.PaymentProcessing = null;
        var poolInfo = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo,
            config.PaymentProcessing);

        Assert.Null(poolInfo.PaymentProcessing);
        foreach(var payload in SerializePoolResponsePayloads(poolInfo,
                    CreateApiJsonOptions(legacyNulls)))
            Assert.False(payload.TryGetProperty("paymentProcessing", out _));
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
    public void ConfigurePayoutSchemeConfig_WithMissingSourcePaymentConfig_OmitsMappedContract()
    {
        var poolInfo = new PoolInfo
        {
            PaymentProcessing = new ApiPoolPaymentProcessingConfig
            {
                Enabled = true,
                MinimumPayment = 1.25m,
            },
        };

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo, null);

        Assert.Null(poolInfo.PaymentProcessing);
    }

    [Fact]
    public void ConfigurePayoutSchemeConfig_WithMissingMappedPaymentConfig_OmitsContract()
    {
        var poolInfo = new PoolInfo();
        var payoutConfig = new PoolPaymentProcessingConfig
        {
            Enabled = true,
            MinimumPayment = 1.25m,
            PayoutScheme = PayoutScheme.SOLO,
        };

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo, payoutConfig);

        Assert.Null(poolInfo.PaymentProcessing);
    }

    [Theory]
    [InlineData(true, PayoutScheme.SOLO, false)]
    [InlineData(true, PayoutScheme.SOLO, true)]
    [InlineData(false, PayoutScheme.PPLNSBF, false)]
    [InlineData(false, PayoutScheme.PPLNSBF, true)]
    public void PoolResponses_PreservePresentPaymentProcessing(
        bool enabled, PayoutScheme payoutScheme, bool legacyNulls)
    {
        var config = CreateMinimalPoolConfig();
        config.PaymentProcessing = new PoolPaymentProcessingConfig
        {
            Enabled = enabled,
            MinimumPayment = 1.25m,
            PayoutScheme = payoutScheme,
            PayoutSchemeConfig = new JObject
            {
                ["factor"] = 3.5m,
                ["blockFinderPercentage"] = 7.5m,
            },
        };
        var poolInfo = config.ToPoolInfo(AutoMapperFactory.CreateMapper(),
            new global::Miningcore.Persistence.Model.PoolStats(), null);

        PoolApiController.ConfigurePayoutSchemeConfig(poolInfo,
            config.PaymentProcessing);

        foreach(var payload in SerializePoolResponsePayloads(poolInfo,
                    CreateApiJsonOptions(legacyNulls)))
        {
            var payment = payload.GetProperty("paymentProcessing");
            Assert.Equal(enabled,
                payment.GetProperty("enabled").GetBoolean());
            Assert.Equal(1.25m,
                payment.GetProperty("minimumPayment").GetDecimal());
            Assert.Equal(payoutScheme.ToString(),
                payment.GetProperty("payoutScheme").GetString());
            var scheme = payment.GetProperty("payoutSchemeConfig");
            Assert.Equal(3.5m, scheme.GetProperty("factor").GetDecimal());

            if(payoutScheme == PayoutScheme.PPLNSBF)
            {
                Assert.Equal(7.5m, scheme
                    .GetProperty("blockFinderPercentage").GetDecimal());
            }
            else
                Assert.False(scheme.TryGetProperty(
                    "blockFinderPercentage", out _));
        }
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

    private static JToken ToWireToken(object value) => value switch
    {
        JToken token => token,
        null => JValue.CreateNull(),
        _ => JToken.FromObject(value),
    };

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

    public sealed record CoerciblePaymentExtraCase(CoinFamily Family,
        string Name, JValue WireValue, object ExpectedClrValue);

    public sealed class PaymentExtraNewtonsoftEnvelope
    {
        public ApiPoolPaymentProcessingExtra Extra { get; set; }
        public JToken Following { get; set; }
    }

    private static object GetPaymentExtraClrValue(
        ApiPoolPaymentProcessingExtra extra, string name) => name switch
        {
            "gas" => extra.Gas,
            "keepUncles" => extra.KeepUncles,
            "walletName" => extra.WalletName,
            "walletAccount" => extra.WalletAccount,
            "minimumConfirmations" => extra.MinimumConfirmations,
            "versionEnablingMaxFee" => extra.VersionEnablingMaxFee,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

    public enum PaymentExtraSourceKind
    {
        Clr,
        DefensiveJToken,
        NewtonsoftConfiguration,
    }
}
