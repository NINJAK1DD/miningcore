using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Miningcore.Api.Extensions;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Ergo.Configuration;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests.Api.Extensions;

public class PaymentProcessingExtraDiagnosticsTests
{
    public static IEnumerable<object[]> RuntimeOnlyCredentialCases()
    {
        yield return new object[] { CoinFamily.Alephium,
            typeof(AlephiumPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Bitcoin,
            typeof(BitcoinPoolPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Equihash,
            typeof(BitcoinPoolPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Ergo,
            typeof(ErgoPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Handshake,
            typeof(HandshakePoolPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Kaspa,
            typeof(KaspaPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Nexa,
            typeof(BitcoinPoolPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Progpow,
            typeof(BitcoinPoolPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Satoshicash,
            typeof(BitcoinPoolPaymentProcessingConfigExtra), "walletPassword" };
        yield return new object[] { CoinFamily.Warthog,
            typeof(WarthogPaymentProcessingConfigExtra), "walletPrivateKey" };
    }

    public static IEnumerable<object[]> CredentialExampleCases()
    {
        yield return new object[] { "alephium_pool.json",
            CoinFamily.Alephium };
        yield return new object[] { "kaspa_pool.json", CoinFamily.Kaspa };
        yield return new object[] { "warthog_pool.json", CoinFamily.Warthog };
    }

    [Fact]
    public void Analyze_DistinguishesOmissionsAndSanitizesDiagnosticKeys()
    {
        const string secretValue = "never-log-this-value";
        var source = new Dictionary<string, object>
        {
            ["Gas"] = new JObject { ["nested"] = 1 },
            ["MaxFeePerGas"] = "not-a-number",
            ["KeepUncles"] = true,
            ["keepuncles"] = false,
            ["minimumConfirmation"] = 12,
            ["walletPassword"] = secretValue,
            ["line\nbreak"] = true,
        };

        var analysis = PaymentProcessingExtraProjection.Analyze(
            CoinFamily.Ethereum, source);

        Assert.Equal(6, analysis.Omissions.Count);
        var ambiguity = Assert.Single(analysis.Omissions.Where(x =>
            x.Outcome == PaymentProcessingExtraProjectionOutcome.
                AmbiguousCaseVariant));
        Assert.Equal(2, ambiguity.VariantCount);
        Assert.Single(analysis.Omissions.Where(x => x.Outcome ==
            PaymentProcessingExtraProjectionOutcome.NonScalarValue));
        Assert.Single(analysis.Omissions.Where(x => x.Outcome ==
            PaymentProcessingExtraProjectionOutcome.ConversionFailure));
        Assert.Equal(3, analysis.Omissions.Count(x => x.Outcome ==
            PaymentProcessingExtraProjectionOutcome.UnknownKey));

        var sensitive = Assert.Single(analysis.Omissions.Where(x =>
            x.KeyWasRedacted));
        Assert.Equal(PaymentProcessingExtraSensitivityPolicy.
            RedactedDiagnosticKey, sensitive.DiagnosticKey);
        Assert.Contains(analysis.Omissions, x =>
            x.DiagnosticKey == "line\\u000Abreak");

        var diagnosticText = string.Join('|', analysis.Omissions.Select(x =>
            x.DiagnosticKey));
        Assert.DoesNotContain("walletPassword", diagnosticText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretValue, diagnosticText,
            StringComparison.Ordinal);
        Assert.Empty(analysis.Projection.PresentProperties);
    }

    [Theory]
    [MemberData(nameof(RuntimeOnlyCredentialCases))]
    public void RuntimeOnlyCredentials_AreRecognizedAndNeverWarned(
        CoinFamily family, Type runtimeType, string configuredName)
    {
        const string secretValue = "runtime-credential-secret";
        var source = new Dictionary<string, object>
        {
            [configuredName] = secretValue,
        };
        var pool = Pool("runtime-only", family, source);
        using var capture = new LogCapture();

        var analysis = PaymentProcessingExtraProjection.Analyze(family,
            source);
        PaymentProcessingExtraDiagnostics.Log(new[] { pool }, capture.Logger);
        capture.Flush();

        Assert.Equal(runtimeType,
            PaymentProcessingExtraProjection.GetRuntimeContractType(family));
        var omission = Assert.Single(analysis.Omissions);
        Assert.Equal(PaymentProcessingExtraProjectionOutcome.RuntimeOnlyKey,
            omission.Outcome);
        Assert.True(omission.KeyWasRedacted);
        Assert.Equal(PaymentProcessingExtraSensitivityPolicy.
            RedactedDiagnosticKey, omission.DiagnosticKey);
        Assert.Empty(capture.Messages);
    }

    [Theory]
    [MemberData(nameof(CredentialExampleCases))]
    public void ShippedCredentialExamples_ProduceNoOmissionWarnings(
        string filename, CoinFamily family)
    {
        var path = FindRepositoryFile(Path.Combine("examples", filename));
        var document = JObject.Parse(File.ReadAllText(path));
        var paymentProcessing = document.SelectToken(
                "pools[0].paymentProcessing")?
            .ToObject<PoolPaymentProcessingConfig>();
        var pool = Pool($"example-{family}", family,
            paymentProcessing?.Extra ?? new Dictionary<string, object>());
        using var capture = new LogCapture();

        PaymentProcessingExtraDiagnostics.Log(new[] { pool }, capture.Logger);
        capture.Flush();

        Assert.NotNull(paymentProcessing?.Extra);
        Assert.Empty(capture.Messages);
    }

    [Fact]
    public void RuntimeOnlyCredentialAmbiguity_ProducesOneRedactedWarning()
    {
        const string firstSecret = "first-runtime-secret";
        const string secondSecret = "second-runtime-secret";
        var source = new Dictionary<string, object>
        {
            ["WalletPassword"] = firstSecret,
            ["walletpassword"] = secondSecret,
        };
        var pool = Pool("runtime-ambiguity", CoinFamily.Kaspa, source);
        using var capture = new LogCapture();

        var analysis = PaymentProcessingExtraProjection.Analyze(
            CoinFamily.Kaspa, source);
        PaymentProcessingExtraDiagnostics.Log(new[] { pool }, capture.Logger);
        capture.Flush();

        var omission = Assert.Single(analysis.Omissions);
        Assert.Equal(PaymentProcessingExtraProjectionOutcome.
            AmbiguousCaseVariant, omission.Outcome);
        Assert.Equal(2, omission.VariantCount);
        Assert.True(omission.KeyWasRedacted);
        var message = Assert.Single(capture.Messages);
        Assert.Contains("reason=ambiguous-case: 2 case variants", message,
            StringComparison.Ordinal);
        Assert.Contains(PaymentProcessingExtraSensitivityPolicy.
            RedactedDiagnosticKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain("WalletPassword", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstSecret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDiagnostics_AreBoundedCredentialSafeAndApiLogSilent()
    {
        const string firstSecret = "first-secret-value";
        const string secondSecret = "second-secret-value";
        const string converterSecret = "converter-exception-secret";
        var first = Pool("first-pool", CoinFamily.Bitcoin,
            new Dictionary<string, object>
            {
                ["walletPassword"] = firstSecret,
                ["minimumConfirmation"] = 12,
                ["Unknown00"] = 0,
                ["Unknown01"] = 1,
                ["Unknown02"] = 2,
                ["Unknown03"] = 3,
                ["Unknown04"] = 4,
                ["Unknown05"] = 5,
                ["Unknown06"] = 6,
                ["Unknown07"] = 7,
                ["Unknown08"] = 8,
                ["Unknown09"] = 9,
                ["Unknown10"] = 10,
            });
        var second = Pool("second-pool", CoinFamily.Ethereum,
            new Dictionary<string, object>
            {
                ["KeepUncles"] = true,
                ["keepuncles"] = false,
                ["Gas"] = new JArray(1, 2),
                ["MaxFeePerGas"] = new ThrowingPaymentValue(
                    converterSecret),
                ["apiSecret"] = secondSecret,
            });
        first.Coin = "bitcoin-test";
        first.Template = null;
        second.Coin = "ethereum-test";
        second.Template = null;

        using var capture = new LogCapture();
        Program.AssignPoolTemplatesAndLogPaymentExtraOmissions(
            new[] { first, second },
            new Dictionary<string, CoinTemplate>
            {
                [first.Coin] = Template(CoinFamily.Bitcoin),
                [second.Coin] = Template(CoinFamily.Ethereum),
            }, capture.Logger);
        var startupLogCount = capture.Messages.Count;

        var mapper = AutoMapperFactory.CreateMapper();
        for(var iteration = 0; iteration < 3; iteration++)
        {
            first.ToPoolInfo(mapper,
                new global::Miningcore.Persistence.Model.PoolStats(), null);
            second.ToPoolInfo(mapper,
                new global::Miningcore.Persistence.Model.PoolStats(), null);
        }

        capture.Flush();
        Assert.Equal(startupLogCount, capture.Messages.Count);

        var firstMessages = capture.Messages.Where(x =>
            x.Contains("first-pool", StringComparison.Ordinal)).ToArray();
        var secondMessages = capture.Messages.Where(x =>
            x.Contains("second-pool", StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, firstMessages.Length);
        Assert.Equal(4, secondMessages.Length);
        Assert.Equal(10, firstMessages.Count(x => x.Contains(
            "is omitted from the public API", StringComparison.Ordinal)));
        Assert.Single(firstMessages.Where(x => x.Contains(
            "additional", StringComparison.Ordinal)));
        Assert.Contains(firstMessages, x => x.Contains(
            "unknown-key=2", StringComparison.Ordinal));
        Assert.Contains(firstMessages, x => x.Contains(
            "minimumConfirmation", StringComparison.Ordinal));
        Assert.Contains(capture.Messages, x => x.Contains(
            "reason=ambiguous-case: 2 case variants",
            StringComparison.Ordinal));
        Assert.Contains(capture.Messages, x => x.Contains(
            "reason=non-scalar", StringComparison.Ordinal));
        Assert.Contains(capture.Messages, x => x.Contains(
            "reason=conversion-failure", StringComparison.Ordinal));
        Assert.Single(capture.Messages.Where(x => x.Contains(
            PaymentProcessingExtraSensitivityPolicy.RedactedDiagnosticKey,
            StringComparison.Ordinal)));

        var allMessages = string.Join('|', capture.Messages);
        Assert.DoesNotContain("walletPassword", allMessages,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiSecret", allMessages,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstSecret, allMessages,
            StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, allMessages,
            StringComparison.Ordinal);
        Assert.DoesNotContain(converterSecret, allMessages,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDiagnostics_UnclassifiedFamilyFailsBeforePoolStartup()
    {
        var pool = Pool("future-family", (CoinFamily) int.MaxValue,
            new Dictionary<string, object>
            {
                ["futureSetting"] = true,
            });
        using var capture = new LogCapture();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PaymentProcessingExtraDiagnostics.Log(new[] { pool },
                capture.Logger));

        Assert.Equal("family", exception.ParamName);
        Assert.Empty(capture.Messages);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PaymentProcessingExtraProjection.Analyze(
                (CoinFamily) int.MaxValue, null));
    }

    [Fact]
    public void StartupDiagnostics_RequiresResolvedTemplate()
    {
        var pool = Pool("missing-template", CoinFamily.Bitcoin,
            new Dictionary<string, object>());
        pool.Template = null;
        using var capture = new LogCapture();

        Assert.Throws<ArgumentNullException>(() =>
            PaymentProcessingExtraDiagnostics.Log(new[] { pool },
                capture.Logger));
        Assert.Empty(capture.Messages);
    }

    [Fact]
    public void SensitivityPolicy_RedactsBeforeTruncatingOrEscaping()
    {
        var sensitiveName = new string('x', 200) + "Token\nTail";

        var sensitive = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey(sensitiveName, out var redacted);
        var ordinary = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey(new string('x', 100) + "\nTail",
                out var ordinaryRedacted);
        var escapeHeavy = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey(new string('\n', 80),
                out var escapeHeavyRedacted);
        var escaped = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey("safe\u2028\u202E'\\tail",
                out var escapedRedacted);
        var spoofedMarker = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey(PaymentProcessingExtraSensitivityPolicy.
                    RedactedDiagnosticKey,
                out var spoofedMarkerRedacted);

        Assert.True(redacted);
        Assert.Equal(PaymentProcessingExtraSensitivityPolicy.
            RedactedDiagnosticKey, sensitive);
        Assert.False(ordinaryRedacted);
        Assert.EndsWith("…", ordinary, StringComparison.Ordinal);
        Assert.True(ordinary.Length <=
            PaymentProcessingExtraSensitivityPolicy.
                MaximumDiagnosticKeyCharacters);
        Assert.False(escapeHeavyRedacted);
        Assert.True(escapeHeavy.Length <=
            PaymentProcessingExtraSensitivityPolicy.
                MaximumDiagnosticKeyCharacters);
        Assert.Contains("\\u000A", escapeHeavy, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", escapeHeavy, StringComparison.Ordinal);
        Assert.False(escapedRedacted);
        Assert.Equal("safe\\u2028\\u202E\\u0027\\u005Ctail", escaped);
        Assert.False(spoofedMarkerRedacted);
        Assert.NotEqual(PaymentProcessingExtraSensitivityPolicy.
            RedactedDiagnosticKey, spoofedMarker);
        Assert.StartsWith("\\u003C", spoofedMarker,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDiagnostics_SanitizesPoolIdentifier()
    {
        var pool = Pool("pool\n'id", CoinFamily.Bitcoin,
            new Dictionary<string, object>
            {
                ["unknown"] = true,
            });
        using var capture = new LogCapture();

        PaymentProcessingExtraDiagnostics.Log(new[] { pool }, capture.Logger);
        capture.Flush();

        var message = Assert.Single(capture.Messages);
        Assert.Contains("pool\\u000A\\u0027id", message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pool\n", message, StringComparison.Ordinal);
    }

    private static PoolConfig Pool(string id, CoinFamily family,
        IDictionary<string, object> extra) => new()
        {
            Id = id,
            Enabled = true,
            Template = Template(family),
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Extra = extra,
            },
        };

    private static string FindRepositoryFile(string relativePath)
    {
        for(var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if(File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Unable to locate repository fixture '{relativePath}'");
    }

    private static CoinTemplate Template(CoinFamily family) =>
        new AlephiumCoinTemplate
        {
            Family = family,
            Name = family.ToString(),
            Symbol = "TEST",
        };

    private sealed class LogCapture : IDisposable
    {
        private readonly LogFactory factory = new();
        private readonly MemoryTarget target = new()
        {
            Layout = "${message}",
        };

        public LogCapture()
        {
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Warn, target);
            factory.Configuration = configuration;
            Logger = factory.GetLogger("payment-extra-diagnostics-test");
        }

        public ILogger Logger { get; }
        public IList<string> Messages => target.Logs;

        public void Flush() => factory.Flush();

        public void Dispose()
        {
            factory.Flush();
            factory.Dispose();
        }
    }

    private sealed class ThrowingPaymentValue(string secret)
    {
        public string Value => throw new InvalidOperationException(secret);
    }
}
