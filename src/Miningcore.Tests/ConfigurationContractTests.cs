using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using FluentValidation.Results;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Blockchain.Beam.Configuration;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Conceal.Configuration;
using Miningcore.Blockchain.Cryptonote.Configuration;
using Miningcore.Blockchain.Equihash.Configuration;
using Miningcore.Blockchain.Ergo.Configuration;
using Miningcore.Blockchain.Ethereum.Configuration;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Satoshicash.Configuration;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Blockchain.Xelis.Configuration;
using Miningcore.Blockchain.Zano.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NBitcoin;
using Xunit;
using Xunit.Sdk;

namespace Miningcore.Tests;

public class ConfigurationContractTests
{
    private const string MissingExamplesDirectoryMarker =
        "<missing-examples-directory>";

    private static readonly IReadOnlyDictionary<string, string>
        ApprovedExampleDonationAddresses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bitcoin"] =
                    "bc1q94x9ncw62g09c80yr38jkewyn6cre3h473g54j",
                ["bitcoin-cash"] =
                    "bitcoincash:qzyvaurh8vlj22jvyhpdce6ld4lt3zfc3svyt665de",
                ["dogecoin"] =
                    "DQKEyZ2sTzcCPeeqzP4xUiPHzwtCS9LUTt",
                ["ethereum"] =
                    "0x4DE55672F0bBB88882A5a589b320eE40FfbdebF9",
                ["ethereumclassic"] =
                    "0x331e6c8d7Caae3Dd1136EefF6c828dBDe5ae64F0",
                ["firo"] =
                    "aH1tURoFqY1quNraAtceE6YFPv3DLFo8zT",
                ["kaspa"] =
                    "kaspa:qzdtdjatlzecrt9u4v22p5vgud6w6ylvemly9df6zpu0gp0yks9xxp24q79pu",
                ["litecoin"] =
                    "ltc1qgnt28drw663gldx76zp3s28xl58wsp0ccv4vxg",
                ["monero"] =
                    "43iiCs5pjvqbzYDvGSPgwtTdR4E4s996cSBsCSTe5HHbSrzr4" +
                    "HBosKZch8t7Fpg34DL9dNcN22T7H6JWEC23B9iDLAZqQsp",
                ["warthog"] =
                    "4701843e274a2a4dfbac59678cb693233274bf5fefcc4e46",
                ["xelis"] =
                    "xel:gt8m2j4al22k8ecp99uducy84vnhn2nlx6ftxjgw2rfr0hg5n47sqkec7n4",
                ["zcash"] =
                    "t1TbjCnoNdGWnwEt9QqCZvHuG3MsWf4Bj66",
            };

    private static readonly Lazy<JObject> BundledCoinTemplates = new(() =>
        JObject.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "coins.json"))));

    private static readonly Lazy<JObject> ConfigSchema = new(() =>
        JObject.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "config.schema.json"))));

    private static readonly DefaultContractResolver ExtensionContractResolver =
        new()
        {
            NamingStrategy = new CamelCaseNamingStrategy(),
        };

    // Runtime extension binding is family-specific. Keep the shipped examples
    // on those same concrete contracts at every extension-data boundary so a
    // correctly spelled field cannot be accepted for the wrong coin family.
    private static readonly Lazy<IReadOnlyDictionary<CoinFamily,
        ExampleExtensionContract>> ExampleExtensionContracts = new(() =>
            new Dictionary<CoinFamily, ExampleExtensionContract>
            {
                [CoinFamily.Alephium] = Contract(
                    new[] { typeof(AlephiumPoolConfigExtra) },
                    new[] { typeof(AlephiumDaemonEndpointConfigExtra) },
                    new[] { typeof(AlephiumPaymentProcessingConfigExtra) }),
                [CoinFamily.Beam] = Contract(
                    new[] { typeof(BeamPoolConfigExtra) },
                    new[] { typeof(BeamDaemonEndpointConfigExtra) }),
                [CoinFamily.Bitcoin] = Contract(
                    new[]
                    {
                        typeof(BitcoinPoolConfigExtra),
                        typeof(MergedMiningPoolConfigExtra),
                    },
                    new[] { typeof(BitcoinDaemonNotificationConfigExtra) },
                    new[] { typeof(BitcoinPoolPaymentProcessingConfigExtra) }),
                [CoinFamily.Conceal] = Contract(
                    new[] { typeof(ConcealPoolConfigExtra) },
                    new[] { typeof(ConcealDaemonEndpointConfigExtra) },
                    new[] { typeof(ConcealPoolPaymentProcessingConfigExtra) }),
                [CoinFamily.BitcoinBlake2b] = Contract(
                    new[] { typeof(BitcoinPoolConfigExtra) },
                    new[] { typeof(BitcoinDaemonNotificationConfigExtra) },
                    new[] { typeof(BitcoinPoolPaymentProcessingConfigExtra) }),
                [CoinFamily.Cryptonote] = Contract(
                    new[] { typeof(CryptonotePoolConfigExtra) },
                    new[] { typeof(CryptonoteDaemonEndpointConfigExtra) },
                    new[]
                    {
                        typeof(CryptonotePoolPaymentProcessingConfigExtra),
                    }),
                [CoinFamily.Equihash] = Contract(
                    new[]
                    {
                        typeof(BitcoinPoolConfigExtra),
                        typeof(EquihashPoolConfigExtra),
                    },
                    new[] { typeof(BitcoinDaemonNotificationConfigExtra) },
                    new[] { typeof(BitcoinPoolPaymentProcessingConfigExtra) }),
                [CoinFamily.Ergo] = Contract(
                    new[] { typeof(ErgoPoolConfigExtra) },
                    new[] { typeof(ErgoDaemonEndpointConfigExtra) },
                    new[] { typeof(ErgoPaymentProcessingConfigExtra) }),
                [CoinFamily.Ethereum] = Contract(
                    new[] { typeof(EthereumPoolConfigExtra) },
                    new[] { typeof(EthereumDaemonEndpointConfigExtra) },
                    new[]
                    {
                        typeof(EthereumPoolPaymentProcessingConfigExtra),
                    }),
                [CoinFamily.Handshake] = Contract(
                    new[] { typeof(BitcoinPoolConfigExtra) },
                    new[] { typeof(BitcoinDaemonNotificationConfigExtra) },
                    new[]
                    {
                        typeof(HandshakePoolPaymentProcessingConfigExtra),
                    }),
                [CoinFamily.Kaspa] = Contract(
                    new[] { typeof(KaspaPoolConfigExtra) },
                    paymentTypes: new[]
                    {
                        typeof(KaspaPaymentProcessingConfigExtra),
                    }),
                [CoinFamily.Nexa] = BitcoinFamilyContract(),
                [CoinFamily.Progpow] = BitcoinFamilyContract(),
                [CoinFamily.Satoshicash] = Contract(
                    new[] { typeof(SatoshicashPoolConfigExtra) },
                    new[] { typeof(BitcoinDaemonNotificationConfigExtra) },
                    new[] { typeof(BitcoinPoolPaymentProcessingConfigExtra) }),
                [CoinFamily.Warthog] = Contract(
                    new[] { typeof(WarthogPoolConfigExtra) },
                    new[] { typeof(WarthogDaemonEndpointConfigExtra) },
                    new[] { typeof(WarthogPaymentProcessingConfigExtra) }),
                [CoinFamily.Xelis] = Contract(
                    new[] { typeof(XelisPoolConfigExtra) },
                    new[] { typeof(XelisDaemonEndpointConfigExtra) },
                    new[] { typeof(XelisPaymentProcessingConfigExtra) }),
                [CoinFamily.Zano] = Contract(
                    new[] { typeof(ZanoPoolConfigExtra) },
                    new[] { typeof(ZanoDaemonEndpointConfigExtra) },
                    new[] { typeof(ZanoPoolPaymentProcessingConfigExtra) }),
            });

    public static IEnumerable<object[]> DateLookingConfigurationStrings()
    {
        yield return new object[] { "2025-01-01" };
        yield return new object[] { "2026-08-16T15:30:00Z" };
        yield return new object[] { "2026-08-16T15:30:00+01:00" };
        yield return new object[] { "2026-08-16T15:30:00" };
    }

    public static IEnumerable<object[]> ShippedExampleConfigurations()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "examples");

        if(!Directory.Exists(directory))
        {
            yield return new object[] { MissingExamplesDirectoryMarker };
            yield break;
        }

        var paths = Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path).Equals(".json",
                StringComparison.Ordinal))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        if(paths.Length == 0)
        {
            yield return new object[] { MissingExamplesDirectoryMarker };
            yield break;
        }

        foreach(var path in paths)
            yield return new object[] { Path.GetFileName(path) };
    }

    private static string FormatValidationErrors(
        IEnumerable<ValidationFailure> errors) =>
        string.Join(Environment.NewLine, errors.Select(error =>
            $"{error.PropertyName}: {error.ErrorMessage}"));

    private static void ValidateNormalStartupWithDiagnostics(
        ClusterConfig config, string context = null)
    {
        try
        {
            Program.ValidateConfig(config, false);
        }

        catch(PoolStartupException error) when (
            string.IsNullOrWhiteSpace(error.Message))
        {
            // ValidateConfig applies live-only defaults before its authoritative
            // validation pass. Re-run that validator against the mutated object so
            // an xUnit failure retains the configuration error instead of exposing
            // the deliberately empty CLI-facing PoolStartupException message.
            var result = new ClusterConfigValidator().Validate(config);
            var diagnostic = result.IsValid
                ? "Full startup validation failed without a diagnostic " +
                  "after applying live defaults."
                : FormatValidationErrors(result.Errors);

            if(!string.IsNullOrEmpty(context))
                diagnostic = $"{context}:{Environment.NewLine}{diagnostic}";

            // The inner-exception constructor became public in xUnit 2.4.2.
            // Keep the test package at or above that version while this helper
            // preserves the originating startup failure as diagnostic context.
            throw new XunitException(diagnostic, error);
        }
    }

    [Fact]
    public void CommittedConfigSchema_MatchesGenerator()
    {
        // The build-output schema is the runtime-shipped artifact. MSBuild refreshes
        // it from src/Miningcore/config.schema.json before this test executes.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));
        var generated = Program.GenerateJsonConfigSchemaDocument();

        Assert.True(JToken.DeepEquals(generated, committed),
            "src/Miningcore/config.schema.json is stale; regenerate it with Miningcore -gcs");

        foreach(var itemTypePath in new[]
                {
                    "definitions.ApiConfig.properties.adminIpWhitelist.items.type",
                    "definitions.ApiConfig.properties.metricsIpWhitelist.items.type",
                    "definitions.ApiRateLimitConfig.properties.ipWhitelist.items.type",
                    "definitions.TcpProxyProtocolConfig.properties.proxyAddresses.items.type",
                    "properties.coinTemplates.items.type",
                })
            Assert.Equal("string",
                committed.SelectToken(itemTypePath)?.Value<string>());
    }

    [Fact]
    public void ConfigSchema_KeepsPoolEndpointNullable_ForDeferredValidation()
    {
        // Miningcore's generated structural schema does not encode the runtime
        // listener predicate: disabled, relay-only and recovery pools deliberately
        // defer endpoint validation. Enforce non-null endpoints in
        // PoolConfigValidator only when a listener will be started.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));

        Assert.Equal(new[] { "object", "null" }, committed.SelectToken(
                "definitions.PoolEndpoint.type")
            ?.Values<string>());
    }

    [Fact]
    public void ConfigSchema_KeepsPoolPaymentProcessingOptional_ForRecovery()
    {
        // Normal startup enforces this live-service contract through the
        // mode-aware validator. Keeping it out of the structural schema lets
        // recovery sanitize damaged payout settings before binding.
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.schema.json");
        var committed = JObject.Parse(File.ReadAllText(path));
        var required = committed.SelectToken(
                "definitions.PoolConfig.required")?
            .Values<string>()
            .ToArray();

        Assert.NotNull(committed.SelectToken(
            "definitions.PoolConfig.properties.paymentProcessing"));
        Assert.DoesNotContain("paymentProcessing",
            required ?? Array.Empty<string>());
        Assert.Equal(new[] { "object", "null" }, committed.SelectToken(
                "definitions.PoolPaymentProcessingConfig.type")?
            .Values<string>());
    }

    [Fact]
    public void ShippedExampleConfig_PassesNormalStartupValidation()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.example.json");
        var document = ParseConfigurationDocument(File.ReadAllText(path));
        var config = Program.ReadConfig(path, false);
        var result = new ClusterConfigValidator().Validate(config);

        // Keep enabled internal listeners on wildcard or loopback addresses. A
        // future specific IPv4 address would make this test depend on the CI
        // runner's active subnet table.
        Assert.True(result.IsValid,
            FormatValidationErrors(result.Errors));
        Assert.All(config.Pools,
            pool => Assert.NotNull(pool.PaymentProcessing));
        AssertOnlyReviewedExtensions("config.example.json", document);
        AssertConfigExampleRewardRecipientPlaceholders(document);
        AssertConfigExampleOperationalPolicy(document);
        AssertConfigExampleAuxiliaryPoolPolicy(document, config);
        AssertExamplePortsDoNotConflict("config.example.json", document,
            config);
        AssertInternalStratumDifficultyTiers("config.example.json",
            document, config);

        // Exercise the actual live-startup boundary as well as the diagnostic-rich
        // direct result above. This applies live defaults, merged-mining checks,
        // API/Stratum conflict detection and the enabled-pool requirement.
        ValidateNormalStartupWithDiagnostics(config);
    }

    [Theory]
    [MemberData(nameof(ShippedExampleConfigurations))]
    public void ShippedTopologyExample_PassesNormalStartupValidation(
        string fileName)
    {
        Assert.True(fileName != MissingExamplesDirectoryMarker,
            "No shipped JSON examples were copied to the test output.");

        var path = Path.Combine(AppContext.BaseDirectory, "examples", fileName);
        var document = ParseConfigurationDocument(File.ReadAllText(path));
        var config = Program.ReadConfig(path, false);
        var result = new ClusterConfigValidator().Validate(config);

        Assert.True(result.IsValid,
            $"{fileName}:{Environment.NewLine}{FormatValidationErrors(result.Errors)}");
        Assert.All(config.Pools,
            pool => Assert.NotNull(pool.PaymentProcessing));

        Assert.All(config.Pools, pool => Assert.True(
            BundledCoinTemplates.Value.ContainsKey(pool.Coin),
            $"{fileName}: pool '{pool.Id}' references unknown bundled coin " +
            $"template '{pool.Coin}'"));

        // Exercise mode-aware defaults and cross-setting contracts, including
        // merged-mining persistence and relay sender/recorder ownership rules.
        ValidateNormalStartupWithDiagnostics(config, fileName);
        AssertShippedExampleOperationalPolicy(fileName, document, config);
    }

    private static void AssertShippedExampleOperationalPolicy(
        string fileName, JObject document, ClusterConfig config)
    {
        AssertOnlyReviewedExtensions(fileName, document);
        Assert.Null(document.SelectToken(
            "paymentProcessing.shareRecoveryFile"));

        var clusterPaymentProcessing = document["paymentProcessing"] as JObject;

        Assert.NotNull(clusterPaymentProcessing);
        Assert.Equal(600,
            clusterPaymentProcessing["interval"]?.Value<int?>());

        if(clusterPaymentProcessing["enabled"]?.Value<bool>() == true)
        {
            Assert.Equal("Miningcore",
                clusterPaymentProcessing["coinbaseString"]?.Value<string>());
        }

        if(document.SelectToken("paymentProcessing.enabled")?.Value<bool>() ==
            true)
        {
            Assert.False(string.IsNullOrWhiteSpace(
                    document["shareRecoveryFile"]?.Value<string>()),
                $"{fileName}: enabled payment processing requires the root " +
                "shareRecoveryFile setting");
        }

        Assert.Null(document["$schema"]);

        if(document.TryGetValue("api", out var apiToken))
        {
            var api = apiToken as JObject;

            Assert.True(api != null,
                $"{fileName}: api must be an object when configured");

            var listenAddress = api["listenAddress"]?.Value<string>();
            var addressIsValid = IPAddress.TryParse(listenAddress,
                out var parsedListenAddress);

            Assert.True(addressIsValid &&
                    IPAddress.IsLoopback(parsedListenAddress),
                $"{fileName}: example API listeners must remain on loopback");

            var ports = new[]
            {
                api["port"]?.Value<int?>(),
                api["adminPort"]?.Value<int?>(),
                api["metricsPort"]?.Value<int?>(),
            };

            Assert.All(ports, port => Assert.True(port is >= 1 and <= 65535,
                $"{fileName}: API ports must be between 1 and 65535"));
            Assert.Equal(ports.Length, ports.Distinct().Count());

            foreach(var whitelistName in new[]
                        {
                            "adminIpWhitelist",
                            "metricsIpWhitelist",
                        })
            {
                var whitelist = api[whitelistName]?.Values<string>() ??
                    Enumerable.Empty<string>();

                Assert.Contains(whitelist, address =>
                    IPAddress.TryParse(address, out var parsedAddress) &&
                    IPAddress.IsLoopback(parsedAddress));
            }

            Assert.True(api.SelectToken("rateLimiting.disabled")?
                    .Value<bool>() == true,
                $"{fileName}: loopback/reverse-proxy examples must delegate " +
                "public-client rate limiting to the proxy");
        }

        var internalPools = config.Pools.Where(pool =>
                pool.Enabled && pool.EnableInternalStratum == true)
            .ToArray();

        if(internalPools.Length > 0)
        {
            Assert.Equal(BanManagerKind.Integrated,
                config.Banning?.Manager);
            Assert.True(config.Banning?.BanOnJunkReceive == true,
                $"{fileName}: internal Stratum requires junk-request banning");
            Assert.True(config.Banning?.BanOnInvalidShares == true,
                $"{fileName}: internal Stratum requires invalid-share banning");
            Assert.True(config.Banning?.BanOnLoginFailure == true,
                $"{fileName}: internal Stratum requires login-failure banning");
            Assert.All(internalPools, pool =>
            {
                Assert.True(pool.Banning?.Enabled == true,
                    $"{fileName}: pool '{pool.Id}' must enable its " +
                    "share-ban threshold");
                Assert.True(pool.Banning.CheckThreshold == 50,
                    $"{fileName}: pool '{pool.Id}' must check share banning " +
                    "after exactly 50 shares");
                Assert.True(pool.Banning.InvalidPercent == 50,
                    $"{fileName}: pool '{pool.Id}' must ban at exactly 50% " +
                    "invalid shares");
                Assert.True(pool.Banning.Time == 600,
                    $"{fileName}: pool '{pool.Id}' must apply a 600-second ban");
            });
        }

        foreach(var credential in document.Descendants()
                    .OfType<JProperty>()
                    .Where(property => new[]
                    {
                        "apiKey",
                        "password",
                        "sharedEncryptionKey",
                        "tlsPfxPassword",
                        "user",
                        "walletPrivateKey",
                        "walletPassword",
                    }.Contains(property.Name,
                        StringComparer.OrdinalIgnoreCase)))
        {
            // Several local daemon examples intentionally use no HTTP Basic
            // authentication. Other configured secret types must never use an
            // empty value because that would weaken this fixture guard.
            var permitsEmptyValue = IsDaemonEndpointCredential(credential);

            if(credential.Value.Type == JTokenType.Null)
            {
                Assert.True(permitsEmptyValue,
                    $"{fileName}: configured secret '{credential.Name}' must " +
                    "use an explicit CHANGE_ME placeholder");
                continue;
            }

            Assert.True(credential.Value.Type == JTokenType.String,
                $"{fileName}: configured secret '{credential.Name}' must be " +
                "a string");

            var credentialValue = credential.Value.Value<string>();

            if(string.IsNullOrWhiteSpace(credentialValue))
            {
                Assert.True(permitsEmptyValue,
                    $"{fileName}: configured secret '{credential.Name}' must " +
                    "use an explicit CHANGE_ME placeholder");
                continue;
            }

            if(credential.Name.Equals("user",
                    StringComparison.OrdinalIgnoreCase) &&
                credentialValue == "miningcore")
                continue;

            Assert.StartsWith("CHANGE_ME_", credentialValue);
        }

        foreach(var pool in document["pools"]?.Children<JObject>() ??
                    Enumerable.Empty<JObject>())
        {
            var poolId = pool["id"]?.Value<string>() ?? "<unknown>";
            var coin = pool["coin"]?.Value<string>() ?? string.Empty;
            var address = pool["address"]?.Value<string>();
            var configuredPool = config.Pools.Single(item =>
                item.Id == poolId);

            Assert.NotNull(pool["enableInternalStratum"]);
            Assert.True(pool["blockRefreshInterval"]?.Value<int?>() >= 0,
                $"{fileName}: pool '{poolId}' must explicitly configure a " +
                "non-negative blockRefreshInterval");
            Assert.True(pool["jobRebroadcastTimeout"]?.Value<int?>() >= 0,
                $"{fileName}: pool '{poolId}' must explicitly configure a " +
                "non-negative jobRebroadcastTimeout");
            if(configuredPool.Enabled &&
               configuredPool.EnableInternalStratum == true)
            {
                var connectionTimeout = pool["clientConnectionTimeout"]?
                    .Value<int?>();
                Assert.True(connectionTimeout is >= 60 and <= 3600,
                    $"{fileName}: pool '{poolId}' must use a reviewed miner " +
                    "connection timeout between 60 and 3600 seconds");
            }

            var paymentProcessing = pool["paymentProcessing"] as JObject;
            Assert.NotNull(paymentProcessing);
            Assert.True(paymentProcessing["minimumPayment"]?.Value<decimal?>() >
                0m, $"{fileName}: pool '{poolId}' must use a positive " +
                "minimumPayment");
            Assert.False(string.IsNullOrWhiteSpace(
                paymentProcessing["payoutScheme"]?.Value<string>()));

            if(string.Equals(coin, "bitcoin", StringComparison.Ordinal) &&
               string.Equals(paymentProcessing["payoutScheme"]?.Value<string>(),
                   "SOLO", StringComparison.Ordinal))
            {
                var expectedDirect = fileName is not
                    ("bitcoin_share_relay_sender.json" or
                     "bitcoin_share_relay_recorder.json");
                Assert.Equal(expectedDirect,
                    pool["soloCoinbasePayout"]?.Value<bool>());
                Assert.Equal(expectedDirect,
                    BitcoinPoolConfigPolicy.ResolveSoloCoinbasePayout(
                        configuredPool, configuredPool.Extra
                            .SafeExtensionDataAs<BitcoinPoolConfigExtra>()));
            }
            else
                Assert.Null(pool["soloCoinbasePayout"]);

            foreach(var daemon in pool["daemons"]?.Children<JObject>() ??
                        Enumerable.Empty<JObject>())
            {
                Assert.Equal("127.0.0.1",
                    daemon["host"]?.Value<string>());
                Assert.InRange(daemon["port"]?.Value<int>() ?? 0,
                    1, ushort.MaxValue);
            }

            if(configuredPool.Enabled &&
                configuredPool.EnableInternalStratum == true)
            {
                Assert.Equal(30,
                    pool["vardiffIdleSweepInterval"]?.Value<int?>());
            }

            Assert.True(address?.StartsWith("CHANGE_ME_",
                    StringComparison.Ordinal) == true,
                $"{fileName}: pool '{poolId}' must use a CHANGE_ME primary " +
                "wallet placeholder");

            if(coin.Equals("bitcoin-cash",
                    StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal("BCash",
                    pool["addressType"]?.Value<string>());
            }

            foreach(var propertyName in new[] { "pubKey", "z-address" })
            {
                var value = pool[propertyName]?.Value<string>();

                if(value != null)
                    Assert.StartsWith("CHANGE_ME_", value);
            }

            var rewardRecipients = pool["rewardRecipients"] as JArray;

            Assert.True(rewardRecipients?.Count > 0,
                $"{fileName}: pool '{poolId}' must demonstrate at least one " +
                "reviewed reward recipient");

            foreach(var recipient in rewardRecipients.Children<JObject>())
            {
                var recipientAddress = recipient["address"]?.Value<string>();
                var percentage = recipient["percentage"]?.Value<decimal?>();
                var isPlaceholder = recipientAddress?.StartsWith("CHANGE_ME_",
                    StringComparison.Ordinal) == true;
                var isApprovedDonation =
                    ApprovedExampleDonationAddresses.TryGetValue(coin,
                        out var approvedAddress) &&
                    recipientAddress == approvedAddress;

                Assert.True(isPlaceholder || isApprovedDonation,
                    $"{fileName}: pool '{poolId}' has an unexpected reward " +
                    $"recipient address '{recipientAddress}'");
                if(fileName == "bitcoin_direct_solo_pool.json")
                {
                    Assert.True(isPlaceholder,
                        $"{fileName}: the direct fee must remain an " +
                        "operator-owned placeholder");
                    Assert.Equal(2m, percentage);
                }
                else
                    Assert.Equal(0m, percentage);
            }
        }

        AssertExamplePortsDoNotConflict(fileName, document, config);
        AssertInternalStratumDifficultyTiers(fileName, document, config);
    }

    [Fact]
    public void RootBitcoinSolo_DeclaresDefaultDirectSettlement()
    {
        var document = ReadExampleConfigDocument();
        var pool = document["pools"]?.Children<JObject>().Single(item =>
            string.Equals(item["coin"]?.Value<string>(), "bitcoin",
                StringComparison.Ordinal));

        Assert.True(pool?["soloCoinbasePayout"]?.Value<bool>());
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsMisplacedDaemonField()
    {
        var document = ReadExampleConfigDocument();
        var pool = Assert.IsType<JObject>(document["pools"]?.First);
        pool["zmqBlockNotifySocket"] = "tcp://127.0.0.1:28332";

        Assert.Contains(
            $"{pool["id"]}.zmqBlockNotifySocket",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void BitcoinExtensionGuard_AcceptsAndBindsPoolMinimumConfirmations()
    {
        var document = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "bitcoin_pool.json")));
        var pool = Assert.IsType<JObject>(document["pools"]?.First);
        pool["minimumConfirmations"] = 120;

        Assert.DoesNotContain($"{pool["id"]}.minimumConfirmations",
            FindInvalidExtensionProperties(document));

        var path = WriteTemporaryConfig(document);

        try
        {
            var config = Program.ReadConfig(path, false);
            var extra = config.Pools.Single().Extra
                .SafeExtensionDataAs<BitcoinPoolConfigExtra>();

            Assert.Equal(120, extra?.MinimumConfirmations);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BitcoinExtensionGuard_RejectsDaemonMinimumConfirmations()
    {
        var document = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "bitcoin_pool.json")));
        var pool = Assert.IsType<JObject>(document["pools"]?.First);
        var daemon = Assert.IsType<JObject>(pool["daemons"]?.First);
        daemon["minimumConfirmations"] = 120;

        Assert.Contains(
            $"{pool["id"]}.daemons[0].minimumConfirmations",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsWrongFamilyProperties()
    {
        var ethereumDocument = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "ethereum_pool.json")));
        var ethereum = Assert.IsType<JObject>(
            ethereumDocument["pools"]?.First);
        var bitcoinDocument = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "bitcoin_pool.json")));
        var bitcoin = Assert.IsType<JObject>(
            bitcoinDocument["pools"]?.First);

        ethereum["addressType"] = "BCash";
        bitcoin["chainTypeOverride"] = "Ethereum";

        var invalid = FindInvalidExtensionProperties(ethereumDocument)
            .Concat(FindInvalidExtensionProperties(bitcoinDocument))
            .ToArray();

        Assert.Contains($"{ethereum["id"]}.addressType", invalid);
        Assert.Contains($"{bitcoin["id"]}.chainTypeOverride", invalid);
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsInvalidValueTypes()
    {
        var document = ReadExampleConfigDocument();
        var bitcoin = document["pools"]?.Children<JObject>().Single(pool =>
            pool["coin"]?.Value<string>() == "bitcoin");
        bitcoin["maxActiveJobs"] = "12";

        Assert.Contains($"{bitcoin["id"]}.maxActiveJobs",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsMisCasedProperty()
    {
        var document = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "firo_pool.json")));
        var pool = Assert.IsType<JObject>(document["pools"]?.First);
        pool["GBTArgs"] = pool["gbtArgs"];
        pool.Remove("gbtArgs");

        Assert.Contains($"{pool["id"]}.GBTArgs",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsMisplacedClusterSetting()
    {
        var document = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "conceal_pool.json")));
        var conceal = Assert.IsType<JObject>(document["pools"]?.First);
        conceal["cryptonightMaxThreads"] = 2;

        Assert.Contains($"{conceal["id"]}.cryptonightMaxThreads",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsNestedExtensionTypo()
    {
        var document = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "dash_pool_no_polling.json")));
        var pool = Assert.IsType<JObject>(document["pools"]?.First);
        var daemon = Assert.IsType<JObject>(pool["daemons"]?.First);
        daemon["zmqBlockNotifySoket"] = daemon["zmqBlockNotifySocket"];
        daemon.Remove("zmqBlockNotifySocket");

        Assert.Contains($"{pool["id"]}.daemons[0].zmqBlockNotifySoket",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void ShippedExampleExtensionGuard_RejectsPaymentExtensionTypo()
    {
        var document = ParseConfigurationDocument(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples",
                "ethereum_pool.json")));
        var pool = Assert.IsType<JObject>(document["pools"]?.First);
        var payment = Assert.IsType<JObject>(pool["paymentProcessing"]);
        payment["maxFeePerGass"] = payment["maxFeePerGas"];
        payment.Remove("maxFeePerGas");

        Assert.Contains($"{pool["id"]}.paymentProcessing.maxFeePerGass",
            FindInvalidExtensionProperties(document));
    }

    [Fact]
    public void ConcealExample_ConfiguresCryptonightConcurrencyAtClusterScope()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples",
            "conceal_pool.json");
        var document = ParseConfigurationDocument(File.ReadAllText(path));
        var config = Program.ReadConfig(path, false);

        Assert.Equal(1, document["cryptonightMaxThreads"]?.Value<int?>());
        Assert.Equal(1, config.CryptonightMaxThreads);
        Assert.Null(document.SelectToken("pools[0].cryptonightMaxThreads"));
    }

    private static void AssertOnlyReviewedExtensions(string fileName,
        JObject document)
    {
        var invalid = FindInvalidExtensionProperties(document);

        Assert.True(invalid.Length == 0,
            $"{fileName}: invalid or unreviewed extension properties: " +
            string.Join(", ", invalid));
    }

    private static string[] FindInvalidExtensionProperties(JObject document)
    {
        var invalid = new List<string>();
        var sharedPoolProperties = GetSchemaProperties("PoolConfig");
        var sharedDaemonProperties = GetSchemaProperties(
            "DaemonEndpointConfig");
        var sharedPaymentProperties = GetSchemaProperties(
            "PoolPaymentProcessingConfig");

        foreach(var pool in document["pools"]?.Children<JObject>() ??
                    Enumerable.Empty<JObject>())
        {
            var poolId = pool["id"]?.Value<string>() ?? "<unknown>";
            var coin = pool["coin"]?.Value<string>();
            var familyName = coin == null
                ? null
                : BundledCoinTemplates.Value[coin]?["family"]?.Value<string>();

            var enumName = familyName == "bitcoin-blake2b"
                ? nameof(CoinFamily.BitcoinBlake2b) : familyName;
            if(!Enum.TryParse<CoinFamily>(enumName, true, out var family) ||
               !ExampleExtensionContracts.Value.TryGetValue(family,
                   out var contract))
            {
                invalid.Add($"{poolId}.<unknown-coin-family>");
                continue;
            }

            ValidateExtensionObject(pool, sharedPoolProperties,
                contract.Pool, poolId, invalid);

            var daemonIndex = 0;
            foreach(var daemon in pool["daemons"]?.Children<JObject>() ??
                        Enumerable.Empty<JObject>())
            {
                ValidateExtensionObject(daemon, sharedDaemonProperties,
                    contract.Daemon, $"{poolId}.daemons[{daemonIndex}]",
                    invalid);
                daemonIndex++;
            }

            if(pool["paymentProcessing"] is JObject paymentProcessing)
            {
                ValidateExtensionObject(paymentProcessing,
                    sharedPaymentProperties, contract.Payment,
                    $"{poolId}.paymentProcessing", invalid);
            }
        }

        return invalid.OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void BitcoinCashExampleDonationAddress_ParsesAsMainnetCashAddr()
    {
        var address = ApprovedExampleDonationAddresses["bitcoin-cash"];

        Assert.NotNull(BitcoinUtils.BCashAddressToDestination(address,
            Network.Main));
    }

    [Fact]
    public void CombinedBitcoinCashExample_PinsDocumentedNodeTopology()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples",
            "bitcoin_bitcoin_cash_pool.json");
        var source = File.ReadAllText(path);
        var document = ParseConfigurationDocument(source);
        var bitcoin = document["pools"]?.Children<JObject>().Single(pool =>
            pool["coin"]?.Value<string>() == "bitcoin");
        var bitcoinCash = document["pools"]?.Children<JObject>().Single(pool =>
            pool["coin"]?.Value<string>() == "bitcoin-cash");
        var bitcoinRpcPort = bitcoin?["daemons"]?.First?["port"]?.Value<int>();
        var bitcoinCashRpcPort = bitcoinCash?["daemons"]?.First?["port"]
            ?.Value<int>();

        Assert.Equal(8332, bitcoinRpcPort);
        Assert.Equal(8432, bitcoinCashRpcPort);
        var sourceLines = Regex.Split(source, "\\r?\\n")
            .Select(line => line.Trim())
            .ToArray();

        Assert.Contains("// datadir=/var/lib/bitcoin-cash", sourceLines);
        Assert.Contains("// rpcport=8432", sourceLines);
        Assert.Contains("// port=8433", sourceLines);
        Assert.Contains("// listenonion=0", sourceLines);
        Assert.DoesNotContain("rpcport=8334", source,
            StringComparison.Ordinal);

        foreach(var relativePath in new[]
                    {
                        Path.Combine("examples", "README.md"),
                        Path.Combine("docs", "releases.md"),
                    })
        {
            var lines = File.ReadAllLines(Path.Combine(
                AppContext.BaseDirectory, relativePath));

            Assert.Contains("datadir=/var/lib/bitcoin-cash", lines);
            Assert.Contains("rpcport=8432", lines);
            Assert.Contains("port=8433", lines);
            Assert.Contains("listenonion=0", lines);
        }
    }

    private static void AssertConfigExampleOperationalPolicy(JObject document)
    {
        var api = Assert.IsType<JObject>(document["api"]);
        var listenAddress = api["listenAddress"]?.Value<string>();

        Assert.True(IPAddress.TryParse(listenAddress,
                out var parsedListenAddress) &&
            IPAddress.IsLoopback(parsedListenAddress),
            "config.example.json: the copy-first API baseline must bind to " +
            "loopback; container exposure requires an explicit operator edit");
        Assert.True(api.SelectToken("rateLimiting.disabled")?
                .Value<bool>() == false,
            "config.example.json: the direct API baseline must keep " +
            "application rate limiting enabled");
    }

    private static void AssertConfigExampleAuxiliaryPoolPolicy(
        JObject document, ClusterConfig config)
    {
        var parent = document["pools"]?.Children<JObject>().Single(pool =>
            pool.SelectToken("mergedMining.auxPoolId")?.Value<string>() ==
            "doge-solo");
        var auxiliary = document["pools"]?.Children<JObject>().Single(pool =>
            pool["id"]?.Value<string>() == "doge-solo");
        var configuredAuxiliary = config.Pools.Single(pool =>
            pool.Id == "doge-solo");
        var endpoint = Assert.Single(
            auxiliary["ports"]?.Children<JProperty>() ??
            Enumerable.Empty<JProperty>());

        Assert.NotNull(parent);
        Assert.False(configuredAuxiliary.EnableInternalStratum);
        Assert.Equal("3042", endpoint.Name);
        Assert.Equal("DOGE-DIRECT-DISABLED",
            endpoint.Value["name"]?.Value<string>());
    }

    private static void AssertConfigExampleRewardRecipientPlaceholders(
        JObject document)
    {
        foreach(var pool in document["pools"]?.Children<JObject>() ??
                    Enumerable.Empty<JObject>())
        {
            var poolId = pool["id"]?.Value<string>() ?? "<unknown>";
            var recipients = pool["rewardRecipients"] as JArray;

            Assert.True(recipients?.Count > 0,
                $"config.example.json: pool '{poolId}' must demonstrate " +
                "a zero-percent reward-recipient placeholder");

            foreach(var recipient in recipients.Children<JObject>())
            {
                Assert.StartsWith("CHANGE_ME_",
                    recipient["address"]?.Value<string>());
                Assert.Equal(0m,
                    recipient["percentage"]?.Value<decimal?>());
            }
        }
    }

    private static void AssertInternalStratumDifficultyTiers(string fileName,
        JObject document, ClusterConfig config)
    {
        foreach(var configuredPool in config.Pools.Where(pool =>
                    pool.Enabled && pool.EnableInternalStratum == true))
        {
            var pool = document["pools"]?.Children<JObject>()
                .Single(item => item["id"]?.Value<string>() ==
                    configuredPool.Id);
            var endpoints = pool?["ports"]?.Children<JProperty>()
                .Where(property => property.Value is JObject)
                .ToArray() ?? Array.Empty<JProperty>();
            var lowMatches = endpoints.Where(endpoint =>
                    endpoint.Value["name"]?.Value<string>()?.EndsWith(
                        "LOW-DIFF", StringComparison.Ordinal) == true)
                .ToArray();
            var highMatches = endpoints.Where(endpoint =>
                    endpoint.Value["name"]?.Value<string>()?.EndsWith(
                        "HIGH-DIFF", StringComparison.Ordinal) == true)
                .ToArray();

            Assert.True(lowMatches.Length == 1,
                $"{fileName}: pool '{configuredPool.Id}' must expose exactly " +
                $"one LOW-DIFF endpoint, found {lowMatches.Length}");
            Assert.True(highMatches.Length == 1,
                $"{fileName}: pool '{configuredPool.Id}' must expose exactly " +
                $"one HIGH-DIFF endpoint, found {highMatches.Length}");

            var low = (JObject) lowMatches[0].Value;
            var high = (JObject) highMatches[0].Value;

            var lowDifficulty = low["difficulty"]?.Value<double>() ?? 0;
            var highDifficulty = high["difficulty"]?.Value<double>() ?? 0;

            Assert.True(lowDifficulty > 0,
                $"{fileName}: pool '{configuredPool.Id}' low tier must use " +
                "a positive initial difficulty");
            Assert.True(highDifficulty > lowDifficulty,
                $"{fileName}: pool '{configuredPool.Id}' high tier must " +
                "start above its low tier");

            foreach(var endpointProperty in endpoints)
            {
                var endpoint = (JObject) endpointProperty.Value;
                var endpointName = endpoint["name"]?.Value<string>() ??
                    "<unnamed>";
                var endpointContext = $"{fileName}: pool " +
                    $"'{configuredPool.Id}' endpoint " +
                    $"'{endpointProperty.Name}' ({endpointName})";
                var varDiff = endpoint["varDiff"] as JObject;
                var difficulty = endpoint["difficulty"]?.Value<double>() ?? 0;
                var minDifficulty = varDiff?["minDiff"]?.Value<double>() ?? 0;

                Assert.True(varDiff != null,
                    $"{endpointContext} must configure VarDiff");
                Assert.True(minDifficulty > 0,
                    $"{endpointContext} must use a positive minDiff");
                Assert.True(minDifficulty <= difficulty,
                    $"{endpointContext} minDiff must not exceed its " +
                    "starting difficulty");

                if(endpointName.EndsWith("LOW-DIFF",
                       StringComparison.Ordinal))
                {
                    Assert.True(minDifficulty < difficulty,
                        $"{endpointContext} must be able to retarget below " +
                        "its starting difficulty");
                }

                Assert.Equal(15d,
                    varDiff["targetTime"]?.Value<double>() ?? double.NaN, 8);
                Assert.Equal(90d,
                    varDiff["retargetTime"]?.Value<double>() ?? double.NaN, 8);
                Assert.Equal(30d,
                    varDiff["variancePercent"]?.Value<double>() ??
                    double.NaN, 8);
            }
        }
    }

    private static void AssertExamplePortsDoNotConflict(string fileName,
        JObject document, ClusterConfig config)
    {
        var bindings = new List<(int Port, string Owner)>();

        if(document["api"] is JObject api)
        {
            foreach(var name in new[] { "port", "adminPort", "metricsPort" })
            {
                if(api[name]?.Value<int?>() is { } port)
                    bindings.Add((port, $"api.{name}"));
            }
        }

        foreach(var pool in document["pools"]?.Children<JObject>() ??
                    Enumerable.Empty<JObject>())
        {
            var poolId = pool["id"]?.Value<string>() ?? "<unknown>";
            var configuredPool = config.Pools.Single(candidate =>
                candidate.Id == poolId);

            var daemonIndex = 0;
            foreach(var daemon in pool["daemons"]?.Children<JObject>() ??
                        Enumerable.Empty<JObject>())
            {
                if(daemon["port"]?.Value<int?>() is { } daemonPort)
                {
                    bindings.Add((daemonPort,
                        $"pool '{poolId}' daemon[{daemonIndex}]"));
                }

                daemonIndex++;
            }

            if(configuredPool.Enabled &&
               configuredPool.EnableInternalStratum == true)
            {
                foreach(var port in pool["ports"]?.Children<JProperty>() ??
                            Enumerable.Empty<JProperty>())
                {
                    Assert.True(int.TryParse(port.Name, out var portNumber),
                        $"{fileName}: pool '{poolId}' has invalid Stratum " +
                        $"port key '{port.Name}'");
                    bindings.Add((portNumber,
                        $"pool '{poolId}' Stratum '{port.Name}'"));
                }
            }
        }

        var collisions = bindings.GroupBy(binding => binding.Port)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: " + string.Join(", ",
                group.Select(binding => binding.Owner)))
            .ToArray();

        Assert.True(collisions.Length == 0,
            $"{fileName}: configured service-port collisions: " +
            string.Join("; ", collisions));
    }

    private static ExampleExtensionContract BitcoinFamilyContract() =>
        Contract(new[] { typeof(BitcoinPoolConfigExtra) },
            new[] { typeof(BitcoinDaemonNotificationConfigExtra) },
            new[] { typeof(BitcoinPoolPaymentProcessingConfigExtra) });

    private static ExampleExtensionContract Contract(Type[] poolTypes = null,
        Type[] daemonTypes = null, Type[] paymentTypes = null) => new(
        CreateExtensionScopeContract(poolTypes),
        CreateExtensionScopeContract(daemonTypes),
        CreateExtensionScopeContract(paymentTypes));

    private static IReadOnlyDictionary<string, Type>
        CreateExtensionScopeContract(IEnumerable<Type> types)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach(var type in types ?? Enumerable.Empty<Type>())
        {
            var contract = Assert.IsType<JsonObjectContract>(
                ExtensionContractResolver.ResolveContract(type));

            foreach(var property in contract.Properties.Where(property =>
                         !property.Ignored && property.Writable &&
                         !string.IsNullOrEmpty(property.PropertyName)))
            {
                Assert.True(!result.TryGetValue(property.PropertyName,
                        out var existingType) ||
                    existingType == property.PropertyType,
                    $"Extension property '{property.PropertyName}' has " +
                    $"conflicting types '{existingType}' and " +
                    $"'{property.PropertyType}'");
                result[property.PropertyName] = property.PropertyType;
            }
        }

        return result;
    }

    private static IReadOnlySet<string> GetSchemaProperties(
        string definitionName)
    {
        var properties = ConfigSchema.Value.SelectToken(
                $"definitions.{definitionName}.properties")?
            .Children<JProperty>()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotNull(properties);
        return properties;
    }

    private static void ValidateExtensionObject(JObject value,
        IReadOnlySet<string> sharedProperties,
        IReadOnlyDictionary<string, Type> extensionProperties, string path,
        ICollection<string> invalid)
    {
        foreach(var property in value.Properties().Where(property =>
                     !sharedProperties.Contains(property.Name)))
        {
            var propertyPath = $"{path}.{property.Name}";

            if(!extensionProperties.TryGetValue(property.Name,
                   out var propertyType))
            {
                invalid.Add(propertyPath);
                continue;
            }

            try
            {
                ValidateExtensionToken(property.Value, propertyType,
                    propertyPath);

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ContractResolver = ExtensionContractResolver,
                    DateParseHandling = DateParseHandling.None,
                    MissingMemberHandling = MissingMemberHandling.Error,
                });
                property.Value.ToObject(propertyType, serializer);
            }
            catch(JsonException)
            {
                invalid.Add(propertyPath);
            }
            catch(InvalidOperationException)
            {
                invalid.Add(propertyPath);
            }
        }
    }

    private static void ValidateExtensionToken(JToken token, Type targetType,
        string path)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var acceptsNull = !targetType.IsValueType || nullableType != null;
        targetType = nullableType ?? targetType;

        if(token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            if(!acceptsNull)
                throw new JsonSerializationException(
                    $"{path} cannot be null");

            return;
        }

        if(typeof(JToken).IsAssignableFrom(targetType) ||
           targetType == typeof(object))
            return;

        if(targetType == typeof(string) || targetType == typeof(char) ||
           targetType == typeof(Guid) || targetType == typeof(Uri) ||
           targetType == typeof(TimeSpan) || targetType == typeof(DateTime) ||
           targetType == typeof(DateTimeOffset))
        {
            RequireTokenType(token, path, JTokenType.String);
            return;
        }

        if(targetType == typeof(bool))
        {
            RequireTokenType(token, path, JTokenType.Boolean);
            return;
        }

        if(targetType.IsEnum)
        {
            RequireTokenType(token, path, JTokenType.String);

            if(!Enum.TryParse(targetType, token.Value<string>(), true,
                   out var enumValue) ||
               !Enum.IsDefined(targetType, enumValue))
            {
                throw new JsonSerializationException(
                    $"{path} is not a defined {targetType.Name} value");
            }

            return;
        }

        if(IsIntegralType(targetType))
        {
            RequireTokenType(token, path, JTokenType.Integer);
            return;
        }

        if(IsFloatingPointType(targetType))
        {
            if(token.Type is not (JTokenType.Integer or JTokenType.Float))
                throw new JsonSerializationException(
                    $"{path} must be numeric");

            return;
        }

        var contract = ExtensionContractResolver.ResolveContract(targetType);

        if(contract is JsonArrayContract arrayContract)
        {
            RequireTokenType(token, path, JTokenType.Array);
            var itemType = arrayContract.CollectionItemType ?? typeof(object);
            var index = 0;

            foreach(var item in token.Children())
            {
                ValidateExtensionToken(item, itemType, $"{path}[{index}]");
                index++;
            }

            return;
        }

        if(contract is JsonDictionaryContract)
        {
            RequireTokenType(token, path, JTokenType.Object);
            return;
        }

        if(contract is JsonObjectContract objectContract)
        {
            RequireTokenType(token, path, JTokenType.Object);
            var objectValue = (JObject) token;
            var properties = objectContract.Properties.Where(property =>
                    !property.Ignored && property.Writable &&
                    !string.IsNullOrEmpty(property.PropertyName))
                .ToDictionary(property => property.PropertyName,
                    StringComparer.Ordinal);

            foreach(var property in objectValue.Properties())
            {
                if(!properties.TryGetValue(property.Name,
                       out var expectedProperty))
                {
                    throw new JsonSerializationException(
                        $"{path}.{property.Name} is not supported");
                }

                ValidateExtensionToken(property.Value,
                    expectedProperty.PropertyType,
                    $"{path}.{property.Name}");
            }
        }
    }

    private static void RequireTokenType(JToken token, string path,
        JTokenType expected)
    {
        if(token.Type != expected)
        {
            throw new JsonSerializationException(
                $"{path} must be {expected}, not {token.Type}");
        }
    }

    private static bool IsIntegralType(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong);

    private static bool IsFloatingPointType(Type type) =>
        type == typeof(float) || type == typeof(double) ||
        type == typeof(decimal);

    private sealed record ExampleExtensionContract(
        IReadOnlyDictionary<string, Type> Pool,
        IReadOnlyDictionary<string, Type> Daemon,
        IReadOnlyDictionary<string, Type> Payment);

    private static bool IsDaemonEndpointCredential(JProperty credential)
    {
        var isUserOrPassword = credential.Name.Equals("password",
                StringComparison.OrdinalIgnoreCase) ||
            credential.Name.Equals("user", StringComparison.OrdinalIgnoreCase);

        if(!isUserOrPassword || credential.Parent?.Parent is not JArray daemons ||
            daemons.Parent is not JProperty daemonsProperty ||
            !daemonsProperty.Name.Equals("daemons",
                StringComparison.OrdinalIgnoreCase) ||
            daemonsProperty.Parent?.Parent is not JArray pools ||
            pools.Parent is not JProperty poolsProperty)
            return false;

        return poolsProperty.Name.Equals("pools",
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(DateLookingConfigurationStrings))]
    public void NormalStartupAndParsedConfigDump_PreserveDateLookingStrings(
        string configuredValue)
    {
        var document = ReadExampleConfigDocument();
        var poolDocument = (JObject)((JArray) document["pools"])[0];
        var paymentDocument = (JObject) poolDocument["paymentProcessing"];
        paymentDocument["walletName"] = configuredValue;
        poolDocument["coin"] = configuredValue;
        document["coinTemplates"] = new JArray(configuredValue);
        var configFile = WriteTemporaryConfig(document);

        try
        {
            var config = Program.ReadAndValidateConfig(configFile, false);
            var pool = config.Pools[0];
            var sourceValue = pool.PaymentProcessing.Extra["walletName"];

            Assert.Equal(configuredValue, pool.Coin);
            Assert.Equal(configuredValue, Assert.Single(config.CoinTemplates));
            Assert.Equal(configuredValue, Assert.IsType<string>(sourceValue));
            Assert.Equal(configuredValue,
                pool.PaymentProcessing.Extra.SafeExtensionDataAs<
                    HandshakePoolPaymentProcessingConfigExtra>()?.WalletName);
            Assert.Equal(document["paymentProcessing"]?["interval"]?.Value<int>(),
                config.PaymentProcessing.Interval);
            Assert.Equal(paymentDocument["minimumPayment"]?.Value<decimal>(),
                pool.PaymentProcessing.MinimumPayment);

            var dumped = ParseConfigurationDocument(
                Program.SerializeParsedConfig(config));
            var dumpedValue = dumped.SelectToken(
                "pools[0].paymentProcessing.walletName");

            Assert.Equal(JTokenType.String, dumpedValue?.Type);
            Assert.Equal(configuredValue, dumpedValue?.Value<string>());
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void ReadConfig_TypedSchemaFailureRetainsPathAndLocation()
    {
        var document = ReadExampleConfigDocument();
        document["paymentProcessing"]["interval"] = "not-an-integer";
        var configFile = WriteTemporaryConfig(document);

        try
        {
            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(configFile, false));

            Assert.Contains("paymentProcessing.interval", error.Message,
                StringComparison.Ordinal);
            Assert.Contains("line", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("position", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(configFile);
        }
    }

    [Fact]
    public void LiveStartupValidationFailureAfterDefaults_RetainsDiagnostic()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.example.json");
        var config = Program.ReadConfig(path, false);
        var internalPools = config.Pools
            .Where(x => x.EnableInternalStratum == true)
            .ToArray();

        Assert.NotEmpty(internalPools);

        var pool = internalPools[0];

        Assert.NotNull(pool.Ports);
        var endpoint = pool.Ports.FirstOrDefault(x => x.Value != null);

        Assert.True(endpoint.Value != null,
            $"The diagnostic fixture requires pool '{pool.Id}' to expose " +
            "at least one non-null Stratum endpoint.");

        pool.EnableInternalStratum = null;
        endpoint.Value.ListenAddress = "stratum.example.com";

        var error = Assert.Throws<XunitException>(() =>
            ValidateNormalStartupWithDiagnostics(config));

        Assert.Contains(
            $"Pool '{pool.Id}' Stratum port {endpoint.Key}: " +
            "listenAddress must be '*' or a valid IPv4/IPv6 address " +
            "(received 'stratum.example.com')",
            error.Message,
            StringComparison.Ordinal);
        Assert.IsType<PoolStartupException>(error.InnerException);
    }

    private static JObject ReadExampleConfigDocument()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "config.example.json");
        using var stream = File.OpenText(path);
        using var reader = ConfigurationJson.CreateReader(stream);

        return JObject.Load(reader, new JsonLoadSettings
        {
            DuplicatePropertyNameHandling =
                DuplicatePropertyNameHandling.Error,
        });
    }

    private static JObject ParseConfigurationDocument(string json)
    {
        using var stream = new StringReader(json);
        using var reader = ConfigurationJson.CreateReader(stream);

        return JObject.Load(reader);
    }

    private static string WriteTemporaryConfig(JObject document)
    {
        var filename = Path.GetTempFileName();
        File.WriteAllText(filename, document.ToString());
        return filename;
    }
}
