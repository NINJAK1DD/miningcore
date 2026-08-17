using System;
using System.Collections.Generic;
using System.Linq;
using Miningcore.Api.Extensions;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests.Api.Extensions;

public class PaymentProcessingExtraDiagnosticsTests
{
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

        Assert.Equal(7, analysis.Omissions.Count);
        Assert.Equal(2, analysis.Omissions.Count(x => x.Outcome ==
            PaymentProcessingExtraProjectionOutcome.AmbiguousCaseVariant));
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
        Program.AssignPoolTemplates(new[] { first, second },
            new Dictionary<string, CoinTemplate>
            {
                [first.Coin] = Template(CoinFamily.Bitcoin),
                [second.Coin] = Template(CoinFamily.Ethereum),
            });
        Program.LogPaymentProcessingExtraOmissions(new[] { first, second },
            capture.Logger);
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
        Assert.Equal(5, secondMessages.Length);
        Assert.Equal(10, firstMessages.Count(x => x.Contains(
            "is omitted from the public API", StringComparison.Ordinal)));
        Assert.Single(firstMessages.Where(x => x.Contains(
            "additional", StringComparison.Ordinal)));
        Assert.Contains(firstMessages, x => x.Contains(
            "unknown-key=3", StringComparison.Ordinal));
        Assert.Contains(firstMessages, x => x.Contains(
            "minimumConfirmation", StringComparison.Ordinal));
        Assert.Contains(capture.Messages, x => x.Contains(
            "reason=ambiguous-case", StringComparison.Ordinal));
        Assert.Contains(capture.Messages, x => x.Contains(
            "reason=non-scalar", StringComparison.Ordinal));
        Assert.Contains(capture.Messages, x => x.Contains(
            "reason=conversion-failure", StringComparison.Ordinal));
        Assert.Equal(2, capture.Messages.Count(x => x.Contains(
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
    public void StartupDiagnostics_ClassificationFailureDoesNotStopStartup()
    {
        const string secretValue = "must-not-enter-the-exception-log";
        var pool = Pool("future-family", (CoinFamily) int.MaxValue,
            new Dictionary<string, object>
            {
                ["walletPassword"] = secretValue,
            });
        using var capture = new LogCapture();

        var exception = Record.Exception(() =>
            Program.LogPaymentProcessingExtraOmissions(new[] { pool },
                capture.Logger));

        Assert.Null(exception);
        var message = Assert.Single(capture.Messages);
        Assert.Contains("could not be classified safely", message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("walletPassword", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretValue, message,
            StringComparison.Ordinal);
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
        var escaped = PaymentProcessingExtraSensitivityPolicy.
            CreateDiagnosticKey("safe\u2028\u202E'\\tail",
                out var escapedRedacted);

        Assert.True(redacted);
        Assert.Equal(PaymentProcessingExtraSensitivityPolicy.
            RedactedDiagnosticKey, sensitive);
        Assert.False(ordinaryRedacted);
        Assert.EndsWith("…", ordinary, StringComparison.Ordinal);
        Assert.True(ordinary.Length <=
            PaymentProcessingExtraSensitivityPolicy.
                MaximumDiagnosticKeyCharacters + 1);
        Assert.False(escapedRedacted);
        Assert.Equal("safe\\u2028\\u202E\\u0027\\u005Ctail", escaped);
    }

    private static PoolConfig Pool(string id, CoinFamily family,
        Dictionary<string, object> extra) => new()
        {
            Id = id,
            Enabled = true,
            Template = Template(family),
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Extra = extra,
            },
        };

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
