using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reactive.Subjects;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public void MergedParentBlock_DefersEffortAndStatusUntilShareSettlement()
    {
        var now = DateTime.UtcNow;
        var block = new Block
        {
            Type = "merged-parent",
            Created = now,
        };

        Assert.True(PayoutManager.ShouldDeferMergedParentShareSettlement(block,
            now.AddSeconds(30)));
        Assert.False(PayoutManager.ShouldDeferMergedParentShareSettlement(block,
            now.Add(PayoutManager.MergedParentShareSettlementDelay)));
    }

    [Fact]
    public void ShareSettlementDelay_AppliesDirectlyButNotToAuxiliaryBlocks()
    {
        var now = DateTime.UtcNow;
        var parent = new Block { Type = "merged-parent", Created = now };
        var auxiliary = new Block { Type = "auxpow", Created = now };

        Assert.True(PayoutManager.ShouldDeferMergedParentShareSettlement(parent, now));
        Assert.False(PayoutManager.ShouldDeferMergedParentShareSettlement(auxiliary, now));
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
    public async Task StartAsync_ReceivesImmediatePoolOnlineAndDisposesSubscription()
    {
        var notifications = new Subject<PoolStatusNotification>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<PoolStatusNotification>().Returns(notifications);
        var fixture = CreateFixture(messageBusOverride: messageBus);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await fixture.Manager.StartAsync(stop.Token);
        Assert.True(notifications.HasObservers);

        notifications.OnNext(new PoolStatusNotification
        {
            Pool = fixture.MiningPool,
            Status = PoolStatus.Online,
        });

        await WaitUntilAsync(() => fixture.Manager.AttachedPoolCount == 1,
            stop.Token);
        await fixture.Manager.StopAsync(stop.Token);

        Assert.False(notifications.HasObservers);
    }

    [Fact]
    public async Task StartAsync_CancellationAfterLeaseAcquisitionDisposesLease()
    {
        var notifications = new Subject<PoolStatusNotification>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<PoolStatusNotification>().Returns(notifications);
        var fixture = CreateFixture(messageBusOverride: messageBus);
        using var canceled = new CancellationTokenSource();
        fixture.PayoutLease.TryAcquireAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                canceled.Cancel();
                return true;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Manager.StartAsync(canceled.Token));

        Assert.False(notifications.HasObservers);
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

    [Theory]
    [InlineData("cryptonote")]
    [InlineData("zano")]
    public async Task MalformedSuccessfulSplitPayout_RetainsDurableOwnership(string family)
    {
        var fixture = CreateFixture();
        var handler = Substitute.For<IPayoutHandler>();
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(
                fixture.Connection, fixture.Pool.Id, Arg.Any<decimal>())
            .Returns(new[] { new Balance { PoolId = fixture.Pool.Id, Address = "miner", Amount = 1 } });

        var error = family == "cryptonote"
            ? Assert.Throws<PayoutOutcomeUncertainException>(() =>
                global::Miningcore.Blockchain.Cryptonote.CryptonotePayoutHandler
                    .ParseTransferSplitSuccess(null))
            : Assert.Throws<PayoutOutcomeUncertainException>(() =>
                global::Miningcore.Blockchain.Zano.ZanoPayoutHandler
                    .ParseTransferSplitSuccess(null));

        handler.PayoutAsync(fixture.MiningPool, Arg.Any<Balance[]>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(error));

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Manager.PayoutPoolBalancesAsync(fixture.MiningPool, fixture.Pool,
                handler, CancellationToken.None));

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FatalPayoutBackgroundFailure_StopsRealHostWithFailureExitCode(
        bool uncertainWalletOutcome)
    {
        var executionStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception failure = uncertainWalletOutcome
            ? new PayoutOutcomeUncertainException("wallet response lost")
            : new InvalidOperationException("payout-manager ownership was lost");
        var fixture = CreateFixture(executeOverride: async _ =>
        {
            executionStarted.TrySetResult(true);
            await releaseFailure.Task;
            throw failure;
        });

        using var host = new HostBuilder()
            .ConfigureServices(services =>
                services.AddSingleton<IHostedService>(fixture.Manager))
            .Build();

        var run = Program.RunStartupBoundaryAsync(
            () => host.RunAsync(),
            _ => Task.CompletedTask,
            () => fixture.ProcessStatus.ExitCode);

        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFailure.TrySetResult(true);

        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, exitCode);
        Assert.Equal(1, fixture.ProcessStatus.ExitCode);
        await fixture.PayoutLease.Received(1).DisposeAsync();
    }

    private static Fixture CreateFixture(BlockStatus persistedStatus = BlockStatus.Pending,
        Func<CancellationToken, Task> executeOverride = null,
        IMessageBus messageBusOverride = null)
    {
        var context = Substitute.For<IComponentContext>();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var shareRepository = Substitute.For<IShareRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var messageBus = messageBusOverride ?? Substitute.For<IMessageBus>();
        var payoutLease = Substitute.For<IPayoutManagerLease>();
        var processStatus = new ProcessStatus();
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

        var manager = executeOverride == null
            ? new PayoutManager(context, connectionFactory, blockRepository,
                shareRepository, balanceRepository, clusterConfig, messageBus, payoutLease,
                processStatus)
            : new PayoutManager(context, connectionFactory, blockRepository,
                shareRepository, balanceRepository, clusterConfig, messageBus, payoutLease,
                processStatus, executeOverride, subscribeToPoolStatus: false);

        return new Fixture(manager, miningPool, pool, block, connection, transaction,
            blockRepository, balanceRepository, messageBus, payoutLease, processStatus);
    }

    private sealed record Fixture(PayoutManager Manager, IMiningPool MiningPool,
        PoolConfig Pool, Block Block, IDbConnection Connection,
        IDbTransaction Transaction, IBlockRepository BlockRepository,
        IBalanceRepository BalanceRepository,
        IMessageBus MessageBus, IPayoutManagerLease PayoutLease,
        ProcessStatus ProcessStatus);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while(!condition())
            await Task.Delay(10, ct);
    }
}
