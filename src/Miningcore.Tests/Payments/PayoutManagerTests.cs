using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutManagerTests
{
    [Fact]
    public async Task RecoveredBlockNotification_IsEmittedAfterTransactionCommit()
    {
        var fixture = CreateFixture();

        await fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
            (_, _) => Task.FromResult(true));

        Received.InOrder(() =>
        {
            fixture.Transaction.Commit();
            fixture.MessageBus.SendMessage(Arg.Any<BlockFoundNotification>(),
                Arg.Any<string>());
        });
    }

    [Fact]
    public async Task RecoveredBlockNotification_IsNotEmittedWhenCommitFails()
    {
        var fixture = CreateFixture();
        fixture.Transaction.When(x => x.Commit())
            .Do(_ => throw new InvalidOperationException("commit failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
                (_, _) => Task.FromResult(true)));

        fixture.Transaction.Received(1).Rollback();
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<BlockFoundNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task UnlockedBlockNotification_IsEmittedAfterTransactionCommit()
    {
        var fixture = CreateFixture();
        fixture.Block.NotifyBlockFoundOnUpdate = false;
        fixture.Block.NotifyBlockUnlockedOnUpdate = true;

        await fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
            (_, _) => Task.FromResult(true));

        Received.InOrder(() =>
        {
            fixture.Transaction.Commit();
            fixture.MessageBus.SendMessage(Arg.Any<BlockUnlockedNotification>(),
                Arg.Any<string>());
        });
    }

    private static Fixture CreateFixture()
    {
        var context = Substitute.For<IComponentContext>();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var shareRepository = Substitute.For<IShareRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var clusterConfig = new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
        };
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate
            {
                Symbol = "DOGE",
                Name = "Dogecoin",
                ExplorerBlockLinks = new Dictionary<string, string>(),
            },
        };
        var block = new Block
        {
            PoolId = pool.Id,
            BlockHeight = 100,
            Miner = "DExampleMiner",
            NotifyBlockFoundOnUpdate = true,
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);

        var manager = new PayoutManager(context, connectionFactory, blockRepository,
            shareRepository, balanceRepository, clusterConfig, messageBus);

        return new Fixture(manager, pool, block, transaction, messageBus);
    }

    private sealed record Fixture(PayoutManager Manager, PoolConfig Pool, Block Block,
        IDbTransaction Transaction, IMessageBus MessageBus);
}
