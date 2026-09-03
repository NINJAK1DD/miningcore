using System;
using System.Collections.Generic;
using System.IO;
using Miningcore;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinDirectSoloConfigurationTests
{
    [Fact]
    public void Option_DefaultsOffAndDoesNotRequireSchema()
    {
        var config = CreateConfig();
        config.Pools[0].Extra = null;

        Program.ValidateBitcoinDirectSoloDeployment(config,
            requireAssignedTemplates: true);

        Assert.False(Program.RequiresBitcoinDirectSoloPersistence(config));
        Assert.False(new BitcoinPoolConfigExtra().SoloCoinbasePayout);
        Assert.True(new BitcoinPoolConfigExtra().Bip54Coinbase);
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
        config.Persistence = null;
        Assert.Contains("requires PostgreSQL",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config)).Message);

        config = CreateConfig();
        config.PaymentProcessing.Enabled = false;
        Assert.Contains("cluster-level payment processing",
            Assert.Throws<PoolStartupException>(() =>
                Program.ValidateBitcoinDirectSoloDeployment(config)).Message);
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
