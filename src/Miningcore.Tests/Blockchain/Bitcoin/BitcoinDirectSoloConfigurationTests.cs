using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Miningcore;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinDirectSoloConfigurationTests
{
    [Fact]
    public void CanonicalBitcoinSolo_DefaultsOnAndRequiresSchema()
    {
        var config = CreateConfig();
        config.Pools[0].Extra = null;

        Program.ValidateBitcoinDirectSoloDeployment(config,
            requireAssignedTemplates: true);

        Assert.True(Program.RequiresBitcoinDirectSoloPersistence(config));
        var defaults = new BitcoinPoolConfigExtra();
        Assert.Null(defaults.SoloCoinbasePayout);
        Assert.Null(defaults.Bip54Coinbase);
        Assert.True(BitcoinPoolConfigPolicy.ResolveSoloCoinbasePayout(
            config.Pools[0], defaults));
        Assert.True(BitcoinPoolConfigPolicy.ResolveBip54Coinbase(
            config.Pools[0], defaults));
    }

    [Fact]
    public async Task ExplicitFalse_RetainsCustodialSettlementWithoutSchema()
    {
        var config = CreateConfig();
        config.Pools[0].Extra["soloCoinbasePayout"] = false;

        Program.ValidateBitcoinDirectSoloDeployment(config,
            requireAssignedTemplates: true);

        Assert.False(Program.RequiresBitcoinDirectSoloPersistence(config));
        await Program.EnsureBitcoinDirectSoloSchemaAsync(config, null, null,
            CancellationToken.None);
    }

    [Fact]
    public void ImplicitDefaultFailure_ExplainsCustodialOptOut()
    {
        var config = CreateConfig();
        config.Pools[0].Extra = null;
        config.Pools[0].EnableInternalStratum = false;

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateBitcoinDirectSoloDeployment(config,
                requireAssignedTemplates: true));

        Assert.Contains("defaulted to direct settlement in v0.3.0",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("set soloCoinbasePayout to false",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitFailure_DoesNotMisreportImplicitDefault()
    {
        var config = CreateConfig();
        config.Pools[0].EnableInternalStratum = false;

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateBitcoinDirectSoloDeployment(config,
                requireAssignedTemplates: true));

        Assert.DoesNotContain("defaulted to direct settlement",
            error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("litecoin", PayoutScheme.SOLO)]
    [InlineData("bitcoin", PayoutScheme.PPS)]
    [InlineData("bitcoin", PayoutScheme.PROP)]
    [InlineData("bitcoin", PayoutScheme.PPLNS)]
    public void Default_DoesNotEscapeCanonicalBitcoinSolo(string coin,
        PayoutScheme scheme)
    {
        var config = CreateConfig();
        config.Pools[0].Coin = coin;
        config.Pools[0].PaymentProcessing.PayoutScheme = scheme;
        config.Pools[0].Extra = null;

        Program.ValidateBitcoinDirectSoloDeployment(config,
            requireAssignedTemplates: true);

        Assert.False(Program.RequiresBitcoinDirectSoloPersistence(config));
    }

    [Fact]
    public void CanonicalBitcoinSolo_RequiresDirectSettlementSchema()
    {
        var config = CreateConfig();

        Program.ValidateBitcoinDirectSoloDeployment(config,
            requireAssignedTemplates: true);

        Assert.True(Program.RequiresBitcoinDirectSoloPersistence(config));
        Assert.True(Program.RequiresSynchronousBlockCandidatePersistence(
            config));
    }

    [Theory]
    [InlineData(PayoutScheme.PPS)]
    [InlineData(PayoutScheme.PROP)]
    [InlineData(PayoutScheme.PPLNS)]
    public void NonSoloScheme_IsRejected(PayoutScheme scheme)
    {
        var config = CreateConfig();
        config.Pools[0].PaymentProcessing.PayoutScheme = scheme;

        Assert.Contains("payoutScheme 'SOLO'",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config,
                    true)).Message);
    }

    [Fact]
    public void NonCanonicalTemplate_IsRejectedAfterAssignment()
    {
        var config = CreateConfig();
        config.Pools[0].Coin = "litecoin";

        Assert.Contains("canonical 'bitcoin' template",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config,
                    true)).Message);
    }

    [Fact]
    public void MissingPostgresOrClusterPaymentProcessing_IsRejected()
    {
        var config = CreateConfig();
        config.Pools[0].Extra = null;
        config.Persistence = null;
        var postgresError = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateBitcoinDirectSoloDeployment(config));
        Assert.Contains("requires PostgreSQL", postgresError.Message);
        Assert.Contains("defaulted to direct settlement in v0.3.0",
            postgresError.Message);

        config = CreateConfig();
        config.Pools[0].Extra = null;
        config.PaymentProcessing.Enabled = false;
        var paymentError = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateBitcoinDirectSoloDeployment(config));
        Assert.Contains("cluster-level payment processing",
            paymentError.Message);
        Assert.Contains("set soloCoinbasePayout to false",
            paymentError.Message);
    }

    [Fact]
    public void ShareRelayAndMergedMiningTopologiesAreRejected()
    {
        var config = CreateConfig();
        config.ShareRelay = new ShareRelayConfig();
        Assert.Contains("does not support share-relay",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config)).Message);

        config = CreateConfig();
        config.Pools[0].Extra["mergedMining"] = new Dictionary<string,
            object>
        {
            ["enabled"] = true,
            ["auxPoolId"] = "doge",
        };
        Assert.Contains("does not support merged-mining",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config)).Message);
    }

    [Fact]
    public void InvalidRecipientContract_IsRejectedBeforeRuntime()
    {
        var config = CreateConfig();
        config.Pools[0].RewardRecipients = new[]
        {
            new RewardRecipient { Address = "fee", Percentage = 100 },
        };

        Assert.Contains("must total less than 100%",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config)).Message);
    }

    [Theory]
    [InlineData("SoloCoinbasePayout", "true", "canonical casing")]
    [InlineData("soloCoinbasePayout", "\"true\"", "JSON Boolean")]
    [InlineData("soloCoinbasePayout", "1", "JSON Boolean")]
    [InlineData("soloCoinbasePayout", "null", "JSON Boolean")]
    [InlineData("soloCoinbasePayout", "{}", "JSON Boolean")]
    [InlineData("soloCoinbasePayout", "[]", "JSON Boolean")]
    public void OptionSyntax_IsStrict(string property, string value,
        string expected)
    {
        var document = JObject.Parse(
            $"{{\"pools\":[{{\"{property}\":{value}}}]}}");

        Assert.Contains(expected, Assert.Throws<JsonSerializationException>(() =>
            Program.ValidateBitcoinDirectSoloSyntax(document)).Message);
    }

    [Fact]
    public void OptionSyntax_AcceptsCanonicalBoolean()
    {
        Program.ValidateBitcoinDirectSoloSyntax(JObject.Parse(
            "{\"pools\":[{\"soloCoinbasePayout\":true}]}"));
    }

    [Fact]
    public void ReadConfig_MiscasedOption_UsesPublicDiagnostic()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path,
                "{\"pools\":[{\"SoloCoinbasePayout\":false}]}");

            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(path, false));

            Assert.StartsWith("Configuration file error:", error.Message,
                StringComparison.Ordinal);
            Assert.Contains("canonical casing 'soloCoinbasePayout'",
                error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FailedExtensionBinding_CannotInvertExplicitChoice()
    {
        var config = CreateConfig();
        config.Pools[0].Extra["maxActiveJobs"] = "not-an-integer";
        var bound = config.Pools[0].Extra.TryExtensionDataAs(
            out BitcoinPoolConfigExtra extra, out var bindingError);

        Assert.False(bound);
        Assert.Null(extra);
        Assert.NotNull(bindingError);
        var error = Assert.Throws<PoolStartupException>(() =>
            BitcoinPoolConfigPolicy.ResolveSoloCoinbasePayout(
                config.Pools[0], extra, bindingError));

        Assert.Contains("could not safely bind Bitcoin pool extension data",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("while soloCoinbasePayout is present",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid extension path: 'maxActiveJobs'",
            error.Message, StringComparison.Ordinal);
        Assert.Same(bindingError, error.InnerException);
    }

    [Fact]
    public void FailedExtensionBinding_CannotInvertBip54CompatibilityChoice()
    {
        var config = CreateConfig();
        config.Pools[0].Extra.Remove("soloCoinbasePayout");
        config.Pools[0].Extra["bip54Coinbase"] = false;
        config.Pools[0].Extra["maxActiveJobs"] = "not-an-integer";
        var bound = config.Pools[0].Extra.TryExtensionDataAs(
            out BitcoinPoolConfigExtra extra, out var bindingError);

        Assert.False(bound);
        Assert.Null(extra);
        Assert.NotNull(bindingError);
        var error = Assert.Throws<PoolStartupException>(() =>
            BitcoinPoolConfigPolicy.ResolveBip54Coinbase(
                config.Pools[0], extra, bindingError));

        Assert.Contains("could not safely bind Bitcoin pool extension data",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("while bip54Coinbase is present",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid extension path: 'maxActiveJobs'",
            error.Message, StringComparison.Ordinal);
        Assert.Same(bindingError, error.InnerException);
    }

    [Theory]
    [InlineData("Bip54Coinbase", "true", "canonical casing")]
    [InlineData("bip54Coinbase", "\"true\"", "JSON Boolean")]
    [InlineData("bip54Coinbase", "1", "JSON Boolean")]
    [InlineData("bip54Coinbase", "null", "JSON Boolean")]
    [InlineData("bip54Coinbase", "{}", "JSON Boolean")]
    [InlineData("bip54Coinbase", "[]", "JSON Boolean")]
    public void Bip54OptionSyntax_IsStrict(string property, string value,
        string expected)
    {
        var document = JObject.Parse(
            $"{{\"pools\":[{{\"{property}\":{value}}}]}}");

        Assert.Contains(expected, Assert.Throws<JsonSerializationException>(() =>
            Program.ValidateBitcoinBip54CoinbaseSyntax(document)).Message);
    }

    [Fact]
    public void Bip54OptionSyntax_AcceptsCanonicalBoolean()
    {
        Program.ValidateBitcoinBip54CoinbaseSyntax(JObject.Parse(
            "{\"pools\":[{\"coin\":\"bitcoin\",\"bip54Coinbase\":false}]}"));
    }

    [Fact]
    public void Bip54OptionSyntax_RejectsNonCanonicalPool()
    {
        var error = Assert.Throws<JsonSerializationException>(() =>
            Program.ValidateBitcoinBip54CoinbaseSyntax(JObject.Parse(
                "{\"pools\":[{\"coin\":\"litecoin\",\"bip54Coinbase\":false}]}")));

        Assert.Contains("exact JSON string 'bitcoin'", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ReadConfig_Bip54OptionWithStructuredCoin_UsesPublicDiagnostic(
        string coin)
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path,
                $"{{\"pools\":[{{\"coin\":{coin},\"bip54Coinbase\":false}}]}}");

            var error = Assert.Throws<PoolStartupException>(() =>
                Program.ReadConfig(path, false));

            Assert.StartsWith("Configuration file error:", error.Message,
                StringComparison.Ordinal);
            Assert.Contains("exact JSON string 'bitcoin'", error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExplicitBip54Option_RequiresCanonicalRuntimeIdentity()
    {
        var config = CreateConfig();
        config.Pools[0].Extra["bip54Coinbase"] = false;
        config.Pools[0].Template.CanonicalName = "Custom Bitcoin";

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateBitcoinBip54CoinbaseDeployment(config,
                requireAssignedTemplates: true));

        Assert.Contains("canonical BTC runtime template", error.Message);
    }

    [Fact]
    public void ExplicitBip54Option_AcceptsAssignedCanonicalRuntimeIdentity()
    {
        var config = CreateConfig();
        config.Pools[0].Extra["bip54Coinbase"] = false;

        Program.ValidateBitcoinBip54CoinbaseDeployment(config,
            requireAssignedTemplates: true);
    }

    [Fact]
    public void ExplicitBip54Option_RequiresTemplateAssignmentAtRuntimeGate()
    {
        var config = CreateConfig();
        config.Pools[0].Extra["bip54Coinbase"] = false;
        config.Pools[0].Template = null;

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateBitcoinBip54CoinbaseDeployment(config,
                requireAssignedTemplates: true));

        Assert.Contains("template was not assigned", error.Message);
    }

    private static ClusterConfig CreateConfig() => new()
    {
        Persistence = new PersistenceConfig { Postgres = new PostgresConfig() },
        PaymentProcessing = new ClusterPaymentProcessingConfig
        {
            Enabled = true,
        },
        Pools = new[]
        {
            new PoolConfig
            {
                Id = "btc-direct",
                Coin = "bitcoin",
                Enabled = true,
                EnableInternalStratum = true,
                Template = new BitcoinTemplate
                {
                    Family = CoinFamily.Bitcoin,
                    Symbol = "BTC",
                    CanonicalName = "Bitcoin",
                },
                PaymentProcessing = new PoolPaymentProcessingConfig
                {
                    Enabled = true,
                    PayoutScheme = PayoutScheme.SOLO,
                },
                Extra = new Dictionary<string, object>
                {
                    ["soloCoinbasePayout"] = true,
                },
            },
        },
    };
}
