using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests;

public class ProgramPoolTemplateTests
{
    [Fact]
    public async Task RecoveryMode_StopsHostAndSkipsNormalBackgroundServices()
    {
        var recovered = false;
        var stopped = false;

        await Program.RunRecoveryModeAsync(() =>
        {
            recovered = true;
            return Task.CompletedTask;
        }, () => stopped = true);

        Assert.True(recovered);
        Assert.True(stopped);
        Assert.False(Program.ShouldConfigureBackgroundServices(true));
        Assert.True(Program.ShouldConfigureBackgroundServices(false));
    }

    [Fact]
    public async Task RecoveryMode_StopsHostWhenImportFails()
    {
        var stopped = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.RunRecoveryModeAsync(
                () => throw new InvalidOperationException("import failed"),
                () => stopped = true));

        Assert.True(stopped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AssignPoolTemplates_IsIndependentOfParentAuxiliaryOrder(bool parentFirst)
    {
        var litecoin = new BitcoinTemplate { Symbol = "LTC", Family = CoinFamily.Bitcoin };
        var dogecoin = new BitcoinTemplate { Symbol = "DOGE", Family = CoinFamily.Bitcoin };
        var parent = new PoolConfig { Id = "ltc-solo", Coin = "litecoin", Enabled = true };
        var auxiliary = new PoolConfig { Id = "doge-solo", Coin = "dogecoin", Enabled = true };
        var pools = parentFirst
            ? new[] { parent, auxiliary }
            : new[] { auxiliary, parent };
        var templates = new Dictionary<string, CoinTemplate>
        {
            ["litecoin"] = litecoin,
            ["dogecoin"] = dogecoin,
        };

        Program.AssignPoolTemplates(pools, templates);

        Assert.Same(litecoin, parent.Template);
        Assert.Same(dogecoin, auxiliary.Template);
    }

    [Fact]
    public void AssignPoolTemplates_RejectsUndefinedCoinBeforePoolStartup()
    {
        var pool = new PoolConfig { Id = "missing", Coin = "undefined", Enabled = true };

        var ex = Assert.Throws<PoolStartupException>(() =>
            Program.AssignPoolTemplates(new[] { pool },
                new Dictionary<string, CoinTemplate>()));

        Assert.Equal(pool.Id, ex.PoolId);
    }

    [Fact]
    public async Task MergedMining_ShareRelaySender_DoesNotRequireLocalPersistenceOrSchemaCheck()
    {
        var config = MergedMiningCluster(shareRelaySender: true,
            globalPaymentProcessing: true, postgres: false);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var blockRepository = Substitute.For<IBlockRepository>();

        Program.ValidateMergedMiningDeployment(config);
        await Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
            blockRepository, CancellationToken.None);

        await connectionFactory.DidNotReceive().OpenConnectionAsync();
        await blockRepository.DidNotReceive().HasMergedMiningBlockIndexesAsync(
            Arg.Any<IDbConnection>(), Arg.Any<CancellationToken>());
        Assert.False(Program.ShouldRunPaymentProcessor(config));
    }

    [Fact]
    public async Task MergedMining_ShareRelaySender_WithPostgresCanRunPayoutManager()
    {
        var config = MergedMiningCluster(shareRelaySender: true);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var blockRepository = Substitute.For<IBlockRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        blockRepository.HasMergedMiningBlockIndexesAsync(connection,
            Arg.Any<CancellationToken>()).Returns(true);

        Assert.True(Program.ShouldRunPaymentProcessor(config));
        Assert.True(Program.RequiresMergedMiningPersistence(config));
        Program.ValidateMergedMiningDeployment(config);
        await Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
            blockRepository, CancellationToken.None);
        await connectionFactory.Received(1).OpenConnectionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaymentProcessor_DirectAndRelayReceiverNodesRemainEnabled(bool relayReceiver)
    {
        var config = MergedMiningCluster(relayReceiver: relayReceiver);

        Assert.True(Program.ShouldRunPaymentProcessor(config));
    }

    [Fact]
    public void PaymentProcessor_NonMergedRelaySenderWithPostgresRetainsOriginalBehaviour()
    {
        var config = MergedMiningCluster(shareRelaySender: true);
        config.Pools[0].Extra = null;

        Assert.True(Program.ShouldRunPaymentProcessor(config));
        Assert.False(Program.RequiresMergedMiningPersistence(config));
    }

    [Fact]
    public void MergedMining_DirectNode_RequiresGlobalPaymentProcessing()
    {
        var config = MergedMiningCluster(globalPaymentProcessing: false);

        var ex = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateMergedMiningDeployment(config));

        Assert.Contains("cluster-level payment processing", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergedMining_RecorderNode_RejectsMissingIndexes(bool relayReceiver)
    {
        var config = MergedMiningCluster(relayReceiver: relayReceiver);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var blockRepository = Substitute.For<IBlockRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        blockRepository.HasMergedMiningBlockIndexesAsync(connection,
            Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<PoolStartupException>(() =>
            Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
                blockRepository, CancellationToken.None));

        Assert.Contains("add_auxpow_block_idempotency.sql", ex.Message);
        Assert.True(Program.ShouldRunPaymentProcessor(config));
        await connectionFactory.Received(1).OpenConnectionAsync();
    }

    private static ClusterConfig MergedMiningCluster(bool shareRelaySender = false,
        bool relayReceiver = false, bool globalPaymentProcessing = true,
        bool postgres = true)
    {
        return new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Id = "ltc-solo",
                    Enabled = true,
                    PaymentProcessing = new PoolPaymentProcessingConfig
                    {
                        Enabled = true,
                    },
                    Extra = new Dictionary<string, object>
                    {
                        ["mergedMining"] = new Dictionary<string, object>
                        {
                            ["enabled"] = true,
                            ["auxPoolId"] = "doge-solo",
                        },
                    },
                },
            },
            ShareRelay = shareRelaySender ? new ShareRelayConfig() : null,
            ShareRelays = relayReceiver ? new[] { new ShareRelayEndpointConfig() } : null,
            PaymentProcessing = new ClusterPaymentProcessingConfig
            {
                Enabled = globalPaymentProcessing,
            },
            Persistence = postgres
                ? new PersistenceConfig { Postgres = new PostgresConfig() }
                : null,
        };
    }
}
