using System;
using Autofac;
using Miningcore;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using System.Collections.Generic;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PpsConfigurationTests
{
    [Fact]
    public void Pps_IsRegisteredAsAProductionPayoutScheme()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<AutofacModule>();
        using var container = builder.Build();

        Assert.True(container.IsRegisteredWithKey<IPayoutScheme>(PayoutScheme.PPS));
    }

    [Fact]
    public void BitcoinPps_IsAcceptedAndRequiresLocalAccountingSchema()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);

        Program.ValidatePpsDeployment(config);

        Assert.True(Program.RequiresShareAccountingPersistence(config));
    }

    [Fact]
    public void RelaySender_RequiresLocalCandidateAndAccountingSchema()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.ShareRelay = new ShareRelayConfig();

        Program.ValidatePpsDeployment(config);

        Assert.True(Program.RequiresShareAccountingPersistence(config));
        Assert.True(Program.RequiresSynchronousBlockCandidatePersistence(config));
        Assert.True(Program.UsesLocalShareRecoveryPath(false, config));
    }

    [Fact]
    public void RelaySenderWithoutPostgres_RejectsPpsBeforeMiningStarts()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.ShareRelay = new ShareRelayConfig();
        config.Persistence = null;

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Contains("Every PPS accepting node requires PostgreSQL", error.Message);
    }

    [Fact]
    public void NonBitcoinPps_FailsBeforeMiningStarts()
    {
        var config = CreateConfig(CoinFamily.Ethereum);

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Contains("Bitcoin-family", error.Message);
    }

    [Fact]
    public void PpsTemplateFamily_IsCheckedAfterProductionAssignment()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.Pools[0].Coin = "litecoin";
        config.Pools[0].Template = null;

        // ReadAndValidateConfig reaches this boundary before LoadCoinTemplates. Template-
        // independent financial checks must still run without rejecting every real PPS config.
        Program.ValidatePpsDeployment(config);

        var missing = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config, requireAssignedTemplates: true));
        Assert.Contains("coin template was not assigned", missing.Message);

        Program.AssignPoolTemplates(config.Pools,
            new Dictionary<string, CoinTemplate>
            {
                ["litecoin"] = new BitcoinTemplate
                {
                    Family = CoinFamily.Bitcoin,
                },
            });

        Program.ValidatePpsDeployment(config, requireAssignedTemplates: true);
    }

    [Fact]
    public void PpsWithNoRetainedReward_FailsBeforeMiningStarts()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.Pools[0].RewardRecipients = new[]
        {
            new RewardRecipient { Address = "fee", Percentage = 100 },
        };

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Contains("no positive operator-funded reward basis", error.Message);
    }

    [Fact]
    public void PpsWithNegativeRewardRecipient_FailsBeforeMiningStarts()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.Pools[0].RewardRecipients = new[]
        {
            new RewardRecipient { Address = "invalid", Percentage = -1 },
        };

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Contains("negative reward-recipient", error.Message);
    }

    [Fact]
    public void PpsWithOverflowingRewardRecipients_FailsWithPoolDiagnostic()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.Pools[0].RewardRecipients = new[]
        {
            new RewardRecipient { Address = "first", Percentage = decimal.MaxValue },
            new RewardRecipient { Address = "second", Percentage = decimal.MaxValue },
        };

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Contains("exceed the supported accounting range", error.Message);
        Assert.Contains("pool", error.Message);
    }

    [Fact]
    public void ExistingSoloSoloMergedMining_DoesNotRequireAccountingMigration()
    {
        var parent = new PoolConfig
        {
            Id = "ltc-solo",
            Enabled = true,
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Extra = new Dictionary<string, object>
            {
                ["mergedMining"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["auxPoolId"] = "doge-solo",
                },
            },
        };
        var auxiliary = new PoolConfig
        {
            Id = "doge-solo",
            Enabled = true,
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
        };
        var config = new ClusterConfig { Pools = new[] { parent, auxiliary } };

        Assert.False(Program.RequiresShareAccountingPersistence(config));

        auxiliary.PaymentProcessing.PayoutScheme = PayoutScheme.PROP;
        Assert.True(Program.RequiresShareAccountingPersistence(config));
    }

    private static ClusterConfig CreateConfig(CoinFamily family) => new()
    {
        Persistence = new PersistenceConfig { Postgres = new PostgresConfig() },
        PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true },
        Pools = new[]
        {
            new PoolConfig
            {
                Id = "pool",
                Enabled = true,
                Template = family == CoinFamily.Bitcoin
                    ? new BitcoinTemplate { Family = family }
                    : new EthereumCoinTemplate { Family = family },
                PaymentProcessing = new PoolPaymentProcessingConfig
                {
                    Enabled = true,
                    PayoutScheme = PayoutScheme.PPS,
                },
            },
        },
    };
}
