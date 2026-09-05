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
    public void BitcoinBlake2bPps_PreservesLocalFinancialStartupRequirements()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.Pools[0].Coin = "bitcoin-blake2b";
        config.Pools[0].Template = new BitcoinBlake2bTemplate { Family = CoinFamily.BitcoinBlake2b };
        Program.ValidatePpsDeployment(config, requireAssignedTemplates: true);
        Assert.True(Program.RequiresShareAccountingPersistence(config));
        Assert.True(Program.RequiresSynchronousBlockCandidatePersistence(config));
        config.ShareRelay = new ShareRelayConfig();
        config.Persistence = null;
        Assert.Throws<PoolStartupException>(() => Program.ValidatePpsDeployment(config));
        config.Persistence = new PersistenceConfig { Postgres = new PostgresConfig() };
        config.PaymentProcessing.Enabled = false;
        Assert.Throws<PoolStartupException>(() => Program.ValidatePpsDeployment(config));
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
    public void PpsWithoutClusterPaymentProcessing_FailsBeforeMiningStarts()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.PaymentProcessing.Enabled = false;

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Contains("PPS requires cluster-level payment processing", error.Message);
        Assert.Contains("liabilities are paid", error.Message);
        Assert.Contains("retention is maintained", error.Message);
    }

    [Fact]
    public void PpsWithoutPoolPaymentProcessing_FailsBeforeMiningStarts()
    {
        var config = CreateConfig(CoinFamily.Bitcoin);
        config.Pools[0].PaymentProcessing.Enabled = false;

        var error = Assert.Throws<PoolStartupException>(() =>
            Program.ValidatePpsDeployment(config));

        Assert.Equal(config.Pools[0].Id, error.PoolId);
        Assert.Contains("uses PPS", error.Message);
        Assert.Contains("must enable pool-level payment processing", error.Message);
        Assert.Contains("before it can accept shares", error.Message);
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
