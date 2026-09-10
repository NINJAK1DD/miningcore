using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using DiagnosticPolicy = Miningcore.Configuration.ConfigurationDiagnosticProjection.DiagnosticPolicy;

namespace Miningcore.Tests;

public class ConfigurationDiagnosticTests
{
    private const string Secret = "ISSUE144_SYNTHETIC_SECRET";

    [Fact]
    public async Task PublicDump_ExampleMatchesReviewedGoldenSnapshot()
    {
        var content = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "config.example.json"));
        var result = await RunDump(content, "--dumpconfig");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Empty(result.Logs);
        var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "config-diagnostics-v2.json"));
        Assert.True(expected.Replace("\r\n", "\n").TrimEnd() == result.Output.Replace("\r\n", "\n").TrimEnd(),
            "The diagnostic snapshot is stale. From the repository root, rebuild Miningcore, then run " +
            "bash scripts/release/update-config-diagnostics-snapshot.sh (Linux), or " +
            "pwsh -NoProfile -File scripts/release/update-config-diagnostics-snapshot.ps1 (PowerShell). " +
            "Review the fixture diff before accepting any diagnostic contract change.");
    }

    [Theory]
    [InlineData("-dc", "=")]
    [InlineData("--dumpconfig", "=")]
    [InlineData("-dc", ":")]
    [InlineData("--dumpconfig", ":")]
    [InlineData("-dc", " ")]
    [InlineData("--dumpconfig", " ")]
    public async Task PublicDump_AllParserSeparatorsStayInsideSafeBoundary(string option, string separator)
    {
        var token = option + separator + Secret;
        Assert.True(Program.LooksLikeDumpConfigOption(token));
        var result = await RunDump(null, token);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Empty(result.Logs);
        Assert.Equal(FailureMessage("usage"), result.Error);
    }

    public static IEnumerable<object[]> InformationCombinations()
    {
        foreach(var dump in new[] { "-dc", "--dumpconfig" })
        foreach(var info in new[] { "-h", "--help", "-?", "-v", "--version" })
        foreach(var first in new[] { true, false })
        foreach(var config in new[] { "absent", "missing", "invalid" })
            yield return new object[] { dump, info, first, config };
    }

    // Keep fast information-path coverage when developers filter ExhaustiveCli.
    [Theory]
    [InlineData("-dc", "-h", true, "invalid")]
    [InlineData("--dumpconfig", "--help", false, "missing")]
    [InlineData("-dc", "-?", false, "absent")]
    [InlineData("--dumpconfig", "-v", true, "invalid")]
    [InlineData("-dc", "--version", false, "missing")]
    [InlineData("--dumpconfig", "--version", true, "absent")]
    [InlineData("-dc", "--help", false, "invalid")]
    [InlineData("--dumpconfig", "-v", false, "invalid")]
    public Task PublicDump_RepresentativeInformationOptions(
        string dump, string info, bool first, string config) =>
        PublicDump_InformationOptionsWinWithoutReadingConfig(dump, info, first, config);

    [Theory]
    [Trait("Category", "ExhaustiveCli")]
    [MemberData(nameof(InformationCombinations))]
    public async Task PublicDump_InformationOptionsWinWithoutReadingConfig(
        string dump, string info, bool first, string config)
    {
        var result = await RunDump(config == "invalid" ? "{\"" + Secret : null,
            arguments: filename => (config == "absent" ? Array.Empty<string>() : new[] { "-c", filename })
                .Concat(first ? new[] { info, dump } : new[] { dump, info }).ToArray());
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Empty(result.Logs);
        Assert.DoesNotContain(Secret, result.Output);
        Assert.DoesNotContain("diagnosticFormatVersion", result.Output);
        Assert.Contains("Miningcore", result.Output);
        if(info is "-h" or "--help" or "-?")
            Assert.Contains("--dumpconfig", result.Output);
    }

    [Theory]
    [InlineData("-rs")]
    [InlineData("--")]
    public async Task PublicDump_ConservativePreScanNeverExecutesOtherCommands(string prefix)
    {
        var result = await RunDump(null, arguments: _ => new[] { prefix, "-dc" });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Empty(result.Logs);
        Assert.Equal(FailureMessage("usage"), result.Error);
    }

    [Fact]
    public async Task PublicDump_PreservesPrecedenceOverSchemaGeneration()
    {
        var result = await RunDump(Fixture().ToString(),
            arguments: filename => new[] { "-gcs", "generated-schema.json", "-c", filename, "-dc" });
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Empty(result.Logs);
        Assert.Equal(2, JObject.Parse(result.Output)["diagnosticFormatVersion"]);
    }

    [Fact]
    public void Projection_AllReviewedMemberTypesHaveAnExplicitSupportedPolicy()
    {
        var objects = ConfigurationDiagnosticProjection.ReviewedObjectTypes.ToHashSet();
        var supported = new HashSet<Type>(objects)
        {
            typeof(bool), typeof(byte), typeof(int), typeof(double), typeof(decimal),
            typeof(PayoutScheme), typeof(BanManagerKind), typeof(PoolConfig[]),
            typeof(DaemonEndpointConfig[]), typeof(RewardRecipient[]),
            typeof(ShareRelayEndpointConfig[]), typeof(Dictionary<int, PoolEndpoint>),
        };
        var fields = ConfigurationDiagnosticProjection.ReviewedMembers.ToArray();
        Assert.NotEmpty(fields);
        Assert.Equal(fields.Length, fields.Select(field => (field.Owner, field.Property.Name)).Distinct().Count());
        foreach(var field in fields)
        {
            Assert.Contains(field.Owner, objects);
            var type = field.Property.PropertyType;
            if(field.Policy is DiagnosticPolicy.Presence or DiagnosticPolicy.Category)
                Assert.Equal(typeof(string), type);
            else if(field.Policy == DiagnosticPolicy.Count)
                Assert.Equal(typeof(string[]), type);
            else
            {
                Assert.Equal(DiagnosticPolicy.Value, field.Policy);
                Assert.True(supported.Contains(Nullable.GetUnderlyingType(type) ?? type),
                    $"Review diagnostic policy after type drift: {field.Owner.Name}.{field.Property.Name} ({type})");
            }
        }
    }

    [Fact]
    public void Projection_EveryPublicPropertyHasExactlyOneExplicitReviewDecision()
    {
        var included = ConfigurationDiagnosticProjection.ReviewedMembers
            .Select(field => (field.Owner, field.Property.Name));
        var excluded = ConfigurationDiagnosticProjection.ExcludedProperties
            .Select(field => (field.Owner, field.Property.Name));
        var decisions = included.Concat(excluded).ToArray();
        var properties = ConfigurationDiagnosticProjection.ReviewedObjectTypes
            .SelectMany(owner => owner.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => (Owner: owner, property.Name))).ToArray();
        var missing = properties.Except(decisions).Select(field => $"{field.Owner.Name}.{field.Name}").ToArray();
        Assert.True(missing.Length == 0,
            "Add an explicit diagnostic policy or exclusion for: " + string.Join(", ", missing));
        Assert.Empty(decisions.Except(properties));
        Assert.Equal(decisions.Length, decisions.Distinct().Count());
    }

    [Fact]
    public void Projection_EmittedKeysIncludingGeneratedSuffixesAreUnique() =>
        Assert.Empty(FindOutputNameCollisions(ConfigurationDiagnosticProjection.ReviewedMembers));

    [Fact]
    public void Projection_CollisionGuardDetectsGeneratedCountKey()
    {
        var owner = typeof(CollisionFixture);
        var collision = Assert.Single(FindOutputNameCollisions(new[]
        {
            (owner, owner.GetProperty(nameof(CollisionFixture.CoinTemplates)), DiagnosticPolicy.Count),
            (owner, owner.GetProperty(nameof(CollisionFixture.CoinTemplatesCount)), DiagnosticPolicy.Value),
        }));
        Assert.Equal((owner, "coinTemplatesCount"), collision.Key);
        Assert.Equal(new[] { nameof(CollisionFixture.CoinTemplates), nameof(CollisionFixture.CoinTemplatesCount) },
            collision.Select(field => field.Property.Name));
    }

    private static IEnumerable<IGrouping<(Type Owner, string Name),
        (Type Owner, PropertyInfo Property, DiagnosticPolicy Policy)>> FindOutputNameCollisions(
        IEnumerable<(Type Owner, PropertyInfo Property, DiagnosticPolicy Policy)> fields) =>
        fields.GroupBy(field => (field.Owner,
                Name: ConfigurationDiagnosticProjection.GetOutputName(field.Owner, field.Property, field.Policy)))
            .Where(group => group.Count() > 1);

    [Theory]
    [InlineData(typeof(ApiConfig), nameof(ApiConfig.ListenAddress), "listenAddressCategory")]
    [InlineData(typeof(PoolEndpoint), nameof(PoolEndpoint.ListenAddress), "listenAddressCategory")]
    [InlineData(typeof(ClusterLoggingConfig), nameof(ClusterLoggingConfig.Level), "level")]
    public void Projection_CategoryOutputNamesAreExplicitPerMember(Type owner, string name, string expected) =>
        Assert.Equal(expected, ConfigurationDiagnosticProjection.GetOutputName(owner, owner.GetProperty(name), DiagnosticPolicy.Category));

    [Fact]
    public void Projection_CategoryNamesDoNotImplicitlyAdmitUnreviewedMembers() =>
        Assert.Throws<KeyNotFoundException>(() => ConfigurationDiagnosticProjection.GetOutputName(
            typeof(CollisionFixture), typeof(CollisionFixture).GetProperty(nameof(CollisionFixture.ListenAddress)), DiagnosticPolicy.Category));

    [Fact]
    public void Projection_CategoryPolicyIsBoundToItsReviewedOwner() =>
        Assert.Throws<KeyNotFoundException>(() => ConfigurationDiagnosticProjection.GetOutputName(
            typeof(PoolEndpoint), typeof(ApiConfig).GetProperty(nameof(ApiConfig.ListenAddress)), DiagnosticPolicy.Category));

    [Fact]
    public void Projection_InvalidPolicyFailsClosed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigurationDiagnosticProjection.GetOutputName(
            typeof(CollisionFixture), typeof(CollisionFixture).GetProperty(nameof(CollisionFixture.CoinTemplates)), (DiagnosticPolicy) int.MaxValue));

    private sealed class CollisionFixture
    {
        public string[] CoinTemplates { get; set; }
        public int CoinTemplatesCount { get; set; }
        public string ListenAddress { get; set; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task PublicDump_ClosedOutputIsNotReportedAsUnreadableConfiguration(string information)
    {
        var result = await RunDump(Fixture().ToString(), arguments: filename => information == null
            ? new[] { "-dc", "-c", filename }
            : new[] { "-dc", information }, closedOutput: true);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Empty(result.Logs);
        Assert.Equal(FailureMessage("output-unavailable"), result.Error);
    }

    [Fact]
    public void FailureCategory_IoErrorsAreScopedToTheFailingStage()
    {
        var error = new IOException(Secret);
        Assert.Equal("usage", Program.GetConfigDiagnosticFailureCategory(Program.ConfigDiagnosticStage.Arguments, error));
        Assert.Equal("unreadable", Program.GetConfigDiagnosticFailureCategory(Program.ConfigDiagnosticStage.Read, error));
        Assert.Equal("internal", Program.GetConfigDiagnosticFailureCategory(Program.ConfigDiagnosticStage.Project, error));
        Assert.Equal("output-unavailable", Program.GetConfigDiagnosticFailureCategory(Program.ConfigDiagnosticStage.Output, error));
    }

    [Fact]
    public void Projection_PropertiesAreSortedAndSourceArrayOrderIsPreserved()
    {
        var config = new ClusterConfig { Pools = new[]
        {
            new PoolConfig { BlockRefreshInterval = 22, Ports = new Dictionary<int, PoolEndpoint>
            {
                [3333] = new PoolEndpoint(), [443] = new PoolEndpoint(),
            }, Daemons = new[]
            {
                new DaemonEndpointConfig { Port = 9002 }, new DaemonEndpointConfig { Port = 9001 },
            } },
            new PoolConfig { BlockRefreshInterval = 11 },
        } };
        var envelope = JObject.Parse(Program.SerializeConfigDiagnostics(config));
        Assert.Equal(new[] { "diagnosticFormatVersion", "notice", "configuration" },
            envelope.Properties().Select(property => property.Name));
        var output = (JObject) envelope["configuration"];
        foreach(var section in output.DescendantsAndSelf().OfType<JObject>())
        {
            var keys = section.Properties().Select(property => property.Name).ToArray();
            if(section.Parent is JProperty { Name: "ports" })
                Assert.Equal(keys.OrderBy(key => int.Parse(key, CultureInfo.InvariantCulture)), keys);
            else
                Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
        }
        Assert.Equal(new[] { "443", "3333" }, ((JObject) output["pools"][0]["ports"])
            .Properties().Select(property => property.Name));
        Assert.Equal(new[] { 22, 11 }, output["pools"].Select(pool => pool["blockRefreshInterval"].Value<int>()));
        Assert.Equal(new[] { 9002, 9001 }, output["pools"][0]["daemons"].Select(daemon => daemon["port"].Value<int>()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "[blank]")]
    [InlineData("  ", "[blank]")]
    [InlineData("\r\n\t\u2003", "[blank]")]
    [InlineData(Secret, "[set]")]
    public void Projection_PresenceNeverEmitsValuesOrClaimsUsability(string value, string expected)
    {
        var config = new ClusterConfig { Persistence = new PersistenceConfig { Postgres = new PostgresConfig
            { TlsCert = value, TlsKey = value, TlsPassword = value, Password = value } } };
        var output = JObject.Parse(Program.SerializeConfigDiagnostics(config))["configuration"]["persistence"]["postgres"];
        foreach(var name in new[] { "tlsCert", "tlsKey", "tlsPassword", "password" })
            Assert.Equal(expected, output[name].Value<string>());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("TRACE", "trace")]
    [InlineData("debug", "debug")]
    [InlineData("info", "info")]
    [InlineData("warn", "warn")]
    [InlineData("error", "error")]
    [InlineData("fatal", "fatal")]
    [InlineData("off", "off")]
    [InlineData(Secret, "[omitted]")]
    public void Projection_LoggingLevelIsAClosedVocabulary(string value, string expected)
    {
        var config = new ClusterConfig { Logging = new ClusterLoggingConfig { Level = value } };
        Assert.Equal(expected, JObject.Parse(Program.SerializeConfigDiagnostics(config))
            ["configuration"]["logging"]["level"].Value<string>());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("*", "any")]
    [InlineData("0.0.0.0", "any")]
    [InlineData("::", "any")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("10.1.2.3", "private")]
    [InlineData("172.16.0.1", "private")]
    [InlineData("192.168.1.1", "private")]
    [InlineData("169.254.1.2", "private")]
    [InlineData("fd00::1", "private")]
    [InlineData("fe80::1", "private")]
    [InlineData("172.15.0.1", "other")]
    [InlineData("172.32.0.1", "other")]
    [InlineData("203.0.113.1", "other")]
    [InlineData("", "blank")]
    [InlineData(" \t\r\n", "blank")]
    [InlineData("100.64.0.1", "other")]
    [InlineData("100.127.255.254", "other")]
    [InlineData(Secret, "other")]
    public void Projection_AddressCategoriesAndArrayCountsNeverEmitContent(string address, string expected)
    {
        var config = new ClusterConfig { CoinTemplates = new[] { Secret },
        Pools = new[] { new PoolConfig { Ports = new Dictionary<int, PoolEndpoint>
            { [3333] = new PoolEndpoint { ListenAddress = address } } } }, Api = new ApiConfig
        {
            ListenAddress = address, AdminIpWhitelist = new[] { Secret, Secret },
            MetricsIpWhitelist = Array.Empty<string>(), RateLimiting = new ApiRateLimitConfig(),
        } };
        var output = JObject.Parse(Program.SerializeConfigDiagnostics(config))["configuration"];
        Assert.Equal(expected, output["api"]["listenAddressCategory"].Value<string>());
        Assert.Equal(expected, output["pools"][0]["ports"]["3333"]["listenAddressCategory"].Value<string>());
        Assert.Null(output["pools"][0]["ports"]["3333"]["listenAddress"]);
        Assert.Equal(1, output["coinTemplatesCount"]);
        Assert.Equal(2, output["api"]["adminIpWhitelistCount"]);
        Assert.Equal(0, output["api"]["metricsIpWhitelistCount"]);
        Assert.Equal(JTokenType.Null, output["api"]["rateLimiting"]["ipWhitelistCount"].Type);
        Assert.DoesNotContain(Secret, output.ToString());
    }

    [Theory]
    [InlineData("-dc")]
    [InlineData("--dumpconfig")]
    public async Task PublicDump_LoadsFileAndExcludesSecretsFromAllOutput(string option)
    {
        var document = Fixture();
        var result = await RunDump(document.ToString(), option);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(string.Empty, result.Logs);
        Assert.DoesNotContain(Secret, result.Output);
        var output = JObject.Parse(result.Output);
        Assert.Equal(2, output["diagnosticFormatVersion"]);
        var config = (JObject) output["configuration"];
        Assert.Equal(document["persistence"]["postgres"]["port"], config["persistence"]["postgres"]["port"]);
        Assert.True(config["api"]["tls"]["enabled"].Value<bool>());
        Assert.Equal(document["paymentProcessing"]["interval"], config["paymentProcessing"]["interval"]);
        Assert.Equal(document["pools"].Count(), config["pools"].Count());
        Assert.Equal("[set]", config["persistence"]["postgres"]["password"]);
        Assert.Null(config["pools"][0]["paymentProcessing"]["payoutSchemeConfig"]);
        Assert.Null(config["pools"][0]["paymentProcessing"]["walletPassword"]);
        Assert.Null(config["pools"][0]["daemons"][0]["apiKey"]);
        Assert.All(config.DescendantsAndSelf().OfType<JValue>()
                .Where(value => value.Type == JTokenType.String),
            value => Assert.Contains(value.Value<string>(),
                new[] { "PPLNS", "PROP", "SOLO", "PPS", "PPBS", "PPLNSBF", "Integrated", "IpTables", "[omitted]", "[set]", "[blank]", "other" }));
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("missing-file")]
    [InlineData("missing-option")]
    [InlineData("unknown-option")]
    [InlineData("invalid-flag-value")]
    public async Task PublicDump_FailuresDoNotEchoInputPathsOrExceptions(string failure)
    {
        var document = Fixture();
        var content = document.ToString();
        switch(failure)
        {
            case "syntax": content = "{\"" + Secret + "\": \"unterminated"; break;
            case "schema": document["api"]["port"] = Secret; content = document.ToString(); break;
            case "duplicate": document["pools"][0]["ID"] = Secret; content = document.ToString(); break;
        }
        var result = await RunDump(content,
            failure == "invalid-flag-value" ? "--dumpconfig=" + Secret : "--dumpconfig", failure);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(string.Empty, result.Logs);
        var category = failure switch
        {
            "syntax" => "invalid-json",
            "schema" => "schema-invalid",
            "duplicate" => "invalid-configuration",
            "missing-file" => "unreadable",
            _ => "usage",
        };
        Assert.Equal(FailureMessage(category), result.Error);
        Assert.DoesNotContain(Secret, result.Error);
        Assert.DoesNotContain("Exception", result.Error);
    }

    [Fact]
    public void Projection_DoesNotMutateRuntimeConfigurationOrSerialization()
    {
        var settings = ConfigurationJson.CreateSerializerSettings();
        var config = JsonConvert.DeserializeObject<ClusterConfig>(Fixture().ToString(), settings);
        var before = JsonConvert.SerializeObject(config, settings);
        var extensions = config.Pools[0].Extra;
        var payout = config.Pools[0].PaymentProcessing.PayoutSchemeConfig;
        var output = Program.SerializeConfigDiagnostics(config);
        Assert.DoesNotContain(Secret, output);
        Assert.Equal(before, JsonConvert.SerializeObject(config, settings));
        Assert.Contains(Secret, before);
        Assert.Same(extensions, config.Pools[0].Extra);
        Assert.Same(payout, config.Pools[0].PaymentProcessing.PayoutSchemeConfig);
        Assert.Equal(output, Program.SerializeConfigDiagnostics(config));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("malformed-json")]
    [InlineData("invalid-schema")]
    [InlineData("valid")]
    public async Task PublicDump_BundledSchemaFailuresArePrivateInstallationErrors(string schema)
    {
        var result = await RunDump(Fixture().ToString(),
            filename => new[] { "--dumpconfig", "-c", filename }, schema: schema);
        Assert.Empty(result.Logs);
        Assert.DoesNotContain(Secret, result.Output + result.Error);
        if(schema == "valid")
        {
            // A control proves the relocated entry assembly can read its schema
            // and execute the real dump, rather than failing during test setup.
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Error);
            Assert.Equal(2, JObject.Parse(result.Output)["diagnosticFormatVersion"].Value<int>());
        }
        else
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Equal(FailureMessage("internal"), result.Error);
        }
    }

    [Fact]
    public void Projection_NeverTraversesUnknownObjectsOrRuntimeTemplates()
    {
        var bomb = new SerializationBomb();
        var config = new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Extra = new Dictionary<string, object> { [Secret] = bomb, ["enabled"] = bomb },
                    Template = new BitcoinTemplate { Extra = new Dictionary<string, object> { [Secret] = bomb } },
                    Daemons = new[] { new DaemonEndpointConfig { Extra = new Dictionary<string, object> { ["port"] = bomb } } },
                    PaymentProcessing = new PoolPaymentProcessingConfig
                    {
                        Extra = new Dictionary<string, object> { ["minimumPayment"] = bomb },
                        PayoutSchemeConfig = new JObject { [Secret] = new JArray(Secret, 1234, true) },
                    },
                },
            },
        };
        Assert.DoesNotContain(Secret, Program.SerializeConfigDiagnostics(config));
        config.Pools = new PoolConfig[] { new UnknownPool() };
        Assert.Equal("[omitted]", JObject.Parse(Program.SerializeConfigDiagnostics(config))
            ["configuration"]["pools"][0].Value<string>());
        Assert.Equal(JTokenType.Null, JObject.Parse(Program.SerializeConfigDiagnostics(null))["configuration"].Type);
    }

    [Fact]
    public void Projection_PreservesReviewedStructureAndBoundsScalarOutput()
    {
        var config = new ClusterConfig
        {
            Logging = new ClusterLoggingConfig { GPDRCompliant = true },
            Banning = new ClusterBanningConfig { Manager = (BanManagerKind) int.MaxValue },
            Pools = new[]
            {
                null,
                new PoolConfig
                {
                    Ports = new Dictionary<int, PoolEndpoint>
                    {
                        [3334] = null,
                        [3333] = new() { Difficulty = double.NaN, VarDiff = new VarDiffConfig { MinDiff = 16 } },
                    },
                    Daemons = Array.Empty<DaemonEndpointConfig>(),
                },
            },
        };
        var output = JObject.Parse(Program.SerializeConfigDiagnostics(config))["configuration"];
        Assert.True(output["logging"]["gpdrCompliant"].Value<bool>());
        Assert.Equal("[omitted]", output["banning"]["manager"]);
        Assert.Equal(JTokenType.Null, output["pools"][0].Type);
        var ports = output["pools"][1]["ports"];
        Assert.Equal("[omitted]", ports["3333"]["difficulty"]);
        Assert.Equal(16, ports["3333"]["varDiff"]["minDiff"].Value<double>());
        Assert.Equal(JTokenType.Null, ports["3334"].Type);
        Assert.Empty(output["pools"][1]["daemons"]);
    }

    [Fact]
    public async Task PublicHelp_DescribesDiagnosticCompatibilityBoundary()
    {
        var result = await RunDump(null, "--help", "help");
        Assert.Equal(0, result.ExitCode);
        var help = Regex.Replace(result.Output, @"\s+", " ");
        Assert.Contains("lossy diagnostic projection", help);
        Assert.Contains("not a configuration export", help);
    }

    private sealed class SerializationBomb
    {
        public string Value => throw new InvalidOperationException(Secret);
        public override string ToString() => throw new InvalidOperationException(Secret);
    }

    private sealed class UnknownPool : PoolConfig
    {
        public string NewCredential => throw new InvalidOperationException(Secret);
    }

    private static JObject Fixture()
    {
        var document = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.example.json")));
        var tls = new JObject { ["enabled"] = true, ["tlsPfxFile"] = Secret, ["tlsPfxPassword"] = Secret };
        document["api"]["tls"] = tls;
        foreach(var name in new[] { "tlsCert", "tlsKey", "tlsPassword" })
            document["persistence"]["postgres"][name] = Secret;
        document["coinTemplates"] = new JArray(Secret);
        document["shareRelay"] = new JObject { ["publishUrl"] = Secret, ["sharedEncryptionKey"] = Secret, ["connect"] = true };
        document["shareRelays"] = new JArray(new JObject { ["url"] = Secret, ["sharedEncryptionKey"] = Secret });
        document["shareRecoveryFile"] = Secret;
        document["shareRecoveryStateDirectory"] = Secret;
        foreach(var pool in document["pools"].Children<JObject>())
        {
            pool["pubKey"] = Secret;
            pool[Secret] = new JObject { ["enabled"] = new JArray(Secret, 987654321, true) };
            pool["paymentProcessing"]["payoutSchemeConfig"] = new JObject { [Secret] = Secret };
            pool["paymentProcessing"]["walletPassword"] = Secret;
            pool["paymentProcessing"]["walletPrivateKey"] = Secret;
            pool["paymentProcessing"][Secret] = new JArray(new JObject { ["port"] = Secret });
            foreach(var port in pool["ports"].Children<JProperty>())
            {
                port.Value["tlsPfxFile"] = Secret;
                port.Value["tlsPfxPassword"] = Secret;
            }
            foreach(var daemon in pool["daemons"].Children<JObject>())
            {
                daemon["apiKey"] = Secret;
                daemon["httpPath"] = Secret;
                daemon[Secret] = new JObject { ["innocent"] = Secret };
            }
        }
        // Strings with innocuous names (host, IDs, URLs, addresses, filenames,
        // extension keys, etc.) are sensitive too. Only bounded enums survive.
        foreach(var value in document.Descendants().OfType<JValue>().ToArray())
            if(value.Type == JTokenType.String &&
               (value.Parent is not JProperty property || property.Name is not ("manager" or "payoutScheme")))
                value.Value = Secret + "\r\n\u2028";
        return document;
    }

    private static Task<(int ExitCode, string Output, string Error, string Logs)> RunDump(
        string content, string option, string mode = null) =>
        RunDump(content, filename =>
        {
            var arguments = new List<string> { option };
            if(mode is not ("missing-option" or "help"))
                arguments.AddRange(new[] { "-c", filename });
            if(mode == "unknown-option")
                arguments.Add("--" + Secret);
            return arguments.ToArray();
        }, createConfig: mode != "missing-file");

    private static async Task<(int ExitCode, string Output, string Error, string Logs)> RunDump(
        string content, Func<string, string[]> arguments, bool createConfig = true, bool closedOutput = false,
        string schema = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "miningcore-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filename = Path.Combine(directory, Secret + ".json");
        var logfile = Path.Combine(directory, "captured.log");
        try
        {
            if(content != null && createConfig)
                await File.WriteAllTextAsync(filename, content);
            if(schema != null)
            {
                var installation = Path.Combine(directory, "installation-" + Secret);
                Directory.CreateDirectory(installation);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"),
                    Path.Combine(installation, "Miningcore.Tests.ProcessHost.dll"));
                var schemaPath = Path.Combine(installation, "config.schema.json");
                switch(schema)
                {
                    case "missing": break;
                    case "directory": Directory.CreateDirectory(schemaPath); break;
                    case "malformed-json": await File.WriteAllTextAsync(schemaPath, "{\"" + Secret); break;
                    case "invalid-schema": await File.WriteAllTextAsync(schemaPath, "{\"type\":\"" + Secret + "\"}"); break;
                    case "valid": File.Copy(Path.Combine(AppContext.BaseDirectory, "config.schema.json"), schemaPath); break;
                    default: throw new ArgumentOutOfRangeException(nameof(schema));
                }
            }
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            foreach(var argument in new[]
            {
                "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.runtimeconfig.json"),
                "--depsfile", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.deps.json"),
                Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"),
                schema != null ? "config-dump-isolated-schema" :
                    closedOutput ? "config-dump-closed-output" : "config-dump", logfile,
            })
                start.ArgumentList.Add(argument);
            foreach(var argument in arguments(filename))
                start.ArgumentList.Add(argument);
            start.Environment["MININGCORE_ADMIN_API_TOKEN"] = Secret;
            start.Environment["PGPASSWORD"] = Secret;
            start.Environment["PGSSLKEY"] = Secret;
            using var process = Process.Start(start);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if(!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }
            }
            Assert.False(File.Exists(Path.Combine(directory, "generated-schema.json")));
            return (process.ExitCode, await stdout, await stderr,
                File.Exists(logfile) ? await File.ReadAllTextAsync(logfile) : string.Empty);
        }
        finally
        {
            // A killed Windows child/file scanner can retain a handle briefly.
            // Cleanup must never mask the failure that caused process termination.
            try { Directory.Delete(directory, true); }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
    }

    private static string FailureMessage(string category) =>
        (category == "output-unavailable"
            ? "Configuration dump failed (output-unavailable). Check the stdout destination or pipeline. Input details are withheld."
            : category == "internal"
            ? "Configuration dump failed (internal). Check the Miningcore installation and diagnostic tooling. Input details are withheld."
            : $"Configuration dump failed ({category}). Supply -c <configfile> with a readable, valid configuration. Input details are withheld. Validate the file privately against config.schema.json and the documented examples; normal startup may reveal detailed errors and start services. Do not publish those logs unreviewed.") + Environment.NewLine;
}
