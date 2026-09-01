using System.Collections.Generic;
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
