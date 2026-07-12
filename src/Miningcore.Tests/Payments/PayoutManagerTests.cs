using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
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

    [Fact]
    public async Task UnlockedBlockNotification_IsNotEmittedWhenCommitFails()
    {
        var fixture = CreateFixture();
        fixture.Block.NotifyBlockFoundOnUpdate = false;
        fixture.Block.NotifyBlockUnlockedOnUpdate = true;
        fixture.Transaction.When(x => x.Commit())
            .Do(_ => throw new InvalidOperationException("commit failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
                (_, _) => Task.FromResult(true)));

        fixture.Transaction.Received(1).Rollback();
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<BlockUnlockedNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PostCommitNotificationFailure_DoesNotPropagate()
    {
        var fixture = CreateFixture();
        fixture.Block.NotifyBlockFoundOnUpdate = false;
        fixture.Block.NotifyBlockUnlockedOnUpdate = true;
        fixture.MessageBus.When(x => x.SendMessage(
                Arg.Any<BlockUnlockedNotification>(), Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("subscriber failed"));

        await fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
            (_, _) => Task.FromResult(true));

        fixture.Transaction.Received(1).Commit();
    }

    [Fact]
    public async Task ConfirmationProgressNotification_IsEmittedAfterTransactionCommit()
    {
        var fixture = CreateFixture();
        fixture.Block.NotifyBlockFoundOnUpdate = false;
        fixture.Block.NotifyBlockConfirmationProgressOnUpdate = true;

        await fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
            (_, _) => Task.FromResult(true));

        Received.InOrder(() =>
        {
            fixture.Transaction.Commit();
            fixture.MessageBus.SendMessage(Arg.Any<BlockConfirmationProgressNotification>(),
                Arg.Any<string>());
        });
    }

    [Fact]
    public async Task ConfirmedBlock_GuardedTransitionPrecedesRewardCredits()
    {
        var fixture = CreateFixture();
        var handler = Substitute.For<IPayoutHandler>();
        var scheme = Substitute.For<IPayoutScheme>();
        fixture.BlockRepository.UpdateBlockAsync(fixture.Connection,
                fixture.Transaction, fixture.Block)
            .Returns(true);
        handler.UpdateBlockRewardBalancesAsync(fixture.Connection, fixture.Transaction,
                fixture.MiningPool, fixture.Block, Arg.Any<CancellationToken>())
            .Returns(12.5m);

        var updated = await fixture.Manager.ApplyConfirmedBlockAsync(fixture.Connection,
            fixture.Transaction, fixture.MiningPool, fixture.Block, handler, scheme,
            CancellationToken.None);

        Assert.True(updated);
        Received.InOrder(() =>
        {
            fixture.BlockRepository.UpdateBlockAsync(fixture.Connection,
                fixture.Transaction, fixture.Block);
            handler.UpdateBlockRewardBalancesAsync(fixture.Connection,
                fixture.Transaction, fixture.MiningPool, fixture.Block,
                Arg.Any<CancellationToken>());
            scheme.UpdateBalancesAsync(fixture.Connection, fixture.Transaction,
                fixture.MiningPool, handler, fixture.Block, 12.5m,
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RejectedClaimPromotion_DoesNotCreditAnyReward()
    {
        var fixture = CreateFixture();
        var handler = Substitute.For<IPayoutHandler>();
        var scheme = Substitute.For<IPayoutScheme>();
        fixture.BlockRepository.UpdateBlockAsync(fixture.Connection,
                fixture.Transaction, fixture.Block)
            .Returns(false);

        var updated = await fixture.Manager.ApplyConfirmedBlockAsync(fixture.Connection,
            fixture.Transaction, fixture.MiningPool, fixture.Block, handler, scheme,
            CancellationToken.None);

        Assert.False(updated);
        await handler.DidNotReceive().UpdateBlockRewardBalancesAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<IMiningPool>(),
            Arg.Any<Block>(), Arg.Any<CancellationToken>());
        await scheme.DidNotReceive().UpdateBalancesAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<IMiningPool>(),
            Arg.Any<IPayoutHandler>(), Arg.Any<Block>(), Arg.Any<decimal>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalBlockRow_PreventsDuplicateCreditAndNotification()
    {
        var fixture = CreateFixture(BlockStatus.Confirmed);
        var actionCalls = 0;

        await fixture.Manager.RunBlockUpdateTransactionAsync(fixture.Pool, fixture.Block,
            (_, _) =>
            {
                actionCalls++;
                return Task.FromResult(true);
            });

        Assert.Equal(0, actionCalls);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<BlockFoundNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task StartAsync_RejectsSecondPayoutManagerOwner()
    {
        var fixture = CreateFixture();
        fixture.PayoutLease.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<PoolStartupException>(() =>
            fixture.Manager.StartAsync(CancellationToken.None));

        Assert.Contains("Another payout manager", ex.Message);
    }

    [Fact]
    public async Task StartAndStop_HoldLeaseForServiceLifetime()
    {
        var fixture = CreateFixture();
        fixture.PayoutLease.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(true);

        await fixture.Manager.StartAsync(CancellationToken.None);
        await fixture.Manager.StopAsync(CancellationToken.None);

        await fixture.PayoutLease.Received(1).TryAcquireAsync(Arg.Any<CancellationToken>());
        await fixture.PayoutLease.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulPayout_CompletesFinancialOperation()
    {
        var fixture = CreateFixture();
        var handler = Substitute.For<IPayoutHandler>();
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(
                fixture.Connection, fixture.Pool.Id, Arg.Any<decimal>())
            .Returns(new[] { new Balance { PoolId = fixture.Pool.Id, Address = "miner", Amount = 1 } });

        await fixture.Manager.PayoutPoolBalancesAsync(fixture.MiningPool, fixture.Pool,
            handler, CancellationToken.None);

        fixture.PayoutLease.Received(1).BeginFinancialOperation();
        fixture.PayoutLease.Received(1).CompleteFinancialOperation();
        fixture.PayoutLease.DidNotReceive().MarkFinancialOutcomeUncertain();
    }

    [Fact]
    public async Task UnknownPayout_MarksFinancialOutcomeUncertain()
    {
        var fixture = CreateFixture();
        var handler = Substitute.For<IPayoutHandler>();
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(
                fixture.Connection, fixture.Pool.Id, Arg.Any<decimal>())
            .Returns(new[] { new Balance { PoolId = fixture.Pool.Id, Address = "miner", Amount = 1 } });
        handler.PayoutAsync(fixture.MiningPool, Arg.Any<Balance[]>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new PayoutOutcomeUncertainException("wallet response lost"));

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Manager.PayoutPoolBalancesAsync(fixture.MiningPool, fixture.Pool,
                handler, CancellationToken.None));

        fixture.PayoutLease.Received(1).BeginFinancialOperation();
        fixture.PayoutLease.Received(1).MarkFinancialOutcomeUncertain();
        fixture.PayoutLease.DidNotReceive().CompleteFinancialOperation();
    }

    [Fact]
    public async Task ConclusivePayoutFailure_CompletesFinancialOperation()
    {
        var fixture = CreateFixture();
        var handler = Substitute.For<IPayoutHandler>();
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(
                fixture.Connection, fixture.Pool.Id, Arg.Any<decimal>())
            .Returns(new[] { new Balance { PoolId = fixture.Pool.Id, Address = "miner", Amount = 1 } });
        handler.PayoutAsync(fixture.MiningPool, Arg.Any<Balance[]>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("invalid wallet configuration"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Manager.PayoutPoolBalancesAsync(fixture.MiningPool, fixture.Pool,
                handler, CancellationToken.None));

        fixture.PayoutLease.Received(1).BeginFinancialOperation();
        fixture.PayoutLease.Received(1).CompleteFinancialOperation();
        fixture.PayoutLease.DidNotReceive().MarkFinancialOutcomeUncertain();
    }

    private static Fixture CreateFixture(BlockStatus persistedStatus = BlockStatus.Pending)
    {
        var context = Substitute.For<IComponentContext>();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var shareRepository = Substitute.For<IShareRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var payoutLease = Substitute.For<IPayoutManagerLease>();
        payoutLease.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(true);
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
            PaymentProcessing = new PoolPaymentProcessingConfig(),
        };
        var miningPool = Substitute.For<IMiningPool>();
        miningPool.Config.Returns(pool);
        var block = new Block
        {
            Id = 42,
            PoolId = pool.Id,
            BlockHeight = 100,
            Miner = "DExampleMiner",
            NotifyBlockFoundOnUpdate = true,
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.GetBlockByIdForUpdateAsync(connection, transaction, block.Id)
            .Returns(new Block
            {
                Id = block.Id,
                PoolId = block.PoolId,
                Status = persistedStatus,
            });

        var manager = new PayoutManager(context, connectionFactory, blockRepository,
            shareRepository, balanceRepository, clusterConfig, messageBus, payoutLease);

        return new Fixture(manager, miningPool, pool, block, connection, transaction,
            blockRepository, balanceRepository, messageBus, payoutLease);
    }

    private sealed record Fixture(PayoutManager Manager, IMiningPool MiningPool,
        PoolConfig Pool, Block Block, IDbConnection Connection,
        IDbTransaction Transaction, IBlockRepository BlockRepository,
        IBalanceRepository BalanceRepository,
        IMessageBus MessageBus, IPayoutManagerLease PayoutLease);
}
