using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using FluentValidation.Results;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;
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
        var config = Program.ReadConfig(path, false);
        var result = new ClusterConfigValidator().Validate(config);

        // Keep enabled internal listeners on wildcard or loopback addresses. A
        // future specific IPv4 address would make this test depend on the CI
        // runner's active subnet table.
        Assert.True(result.IsValid,
            FormatValidationErrors(result.Errors));
        Assert.All(config.Pools,
            pool => Assert.NotNull(pool.PaymentProcessing));

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
        Assert.Null(document.SelectToken(
            "paymentProcessing.shareRecoveryFile"));

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
            Assert.All(internalPools, pool => Assert.True(
                pool.Banning?.Enabled == true,
                $"{fileName}: pool '{pool.Id}' must enable its share-ban threshold"));
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
            if(credential.Value.Type != JTokenType.String)
                continue;

            var credentialValue = credential.Value.Value<string>();
            // Several local daemon examples intentionally use no HTTP Basic
            // authentication. Other configured secret types must never use an
            // empty value because that would weaken this fixture guard.
            var permitsEmptyValue = credential.Name.Equals("password",
                    StringComparison.OrdinalIgnoreCase) ||
                credential.Name.Equals("user",
                    StringComparison.OrdinalIgnoreCase);

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

            Assert.True(address?.StartsWith("CHANGE_ME_",
                    StringComparison.Ordinal) == true,
                $"{fileName}: pool '{poolId}' must use a CHANGE_ME primary " +
                "wallet placeholder");

            foreach(var propertyName in new[] { "pubKey", "z-address" })
            {
                var value = pool[propertyName]?.Value<string>();

                if(value != null)
                    Assert.StartsWith("CHANGE_ME_", value);
            }

            foreach(var recipient in pool["rewardRecipients"]?
                        .Children<JObject>() ?? Enumerable.Empty<JObject>())
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
                Assert.Equal(0m, percentage);
            }
        }
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
