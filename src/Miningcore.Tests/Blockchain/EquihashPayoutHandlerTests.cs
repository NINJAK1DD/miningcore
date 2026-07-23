using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Equihash.Configuration;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class EquihashPayoutHandlerTests
{
    [Fact]
    public async Task PayoutAsync_UncertainSubmissionStillRelocksWithFreshToken()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var (handler, pool) = CreateHandler(messageBus);
        handler.SetPayoutAction((_, _, _) =>
        {
            handler.MarkWalletUnlocked();
            return Task.FromException(new PayoutOutcomeUncertainException(
                "wallet response lost"));
        });
        CancellationToken relockToken = default;
        handler.SetLockAction(ct =>
        {
            relockToken = ct;
            return Task.CompletedTask;
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var thrown = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { Balance(pool.Id) }, cancelled.Token));

        Assert.Contains("wallet response lost", thrown.Message);
        Assert.True(relockToken.CanBeCanceled);
        Assert.False(relockToken.IsCancellationRequested);
        messageBus.DidNotReceive().SendMessage(
            Arg.Any<AdminNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutAsync_SuccessThenLockFailureDoesNotReportPayoutFailure()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var (handler, pool) = CreateHandler(messageBus);
        handler.SetPayoutAction((_, balances, _) =>
        {
            handler.MarkWalletUnlocked();
            handler.QueueSuccess(balances, "tx-accepted");
            return Task.CompletedTask;
        });
        handler.SetLockAction(_ =>
            Task.FromException(new InvalidOperationException("lock unavailable")));

        await handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { Balance(pool.Id) }, CancellationToken.None);

        messageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success),
            Arg.Any<string>());
        messageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure),
            Arg.Any<string>());
        messageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x =>
                x.Subject == "Payout wallet relock failed" &&
                x.Message.Contains("lock unavailable")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutAsync_WalletNeverUnlockedDoesNotAttemptRelock()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var (handler, pool) = CreateHandler(messageBus);
        handler.SetPayoutAction((_, _, _) => Task.CompletedTask);
        var relockCalls = 0;
        handler.SetLockAction(_ =>
        {
            relockCalls++;
            return Task.CompletedTask;
        });

        await handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { Balance(pool.Id) }, CancellationToken.None);

        Assert.Equal(0, relockCalls);
        messageBus.DidNotReceive().SendMessage(
            Arg.Any<AdminNotification>(), Arg.Any<string>());
    }

    private static (TestEquihashPayoutHandler Handler, PoolConfig Pool)
        CreateHandler(IMessageBus messageBus)
    {
        var handler = new TestEquihashPayoutHandler(
            Substitute.For<IComponentContext>(), Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
            messageBus, Substitute.For<IActiveBlockGracePeriodTracker>());
        var pool = new PoolConfig
        {
            Id = "equihash-test",
            Template = new EquihashCoinTemplate { Symbol = "ZEC" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool);
        return (handler, pool);
    }

    private static Balance Balance(string poolId) => new()
    {
        PoolId = poolId,
        Address = "t-test-recipient",
        Amount = 1,
    };

    private sealed class TestEquihashPayoutHandler : EquihashPayoutHandler
    {
        private Func<IMiningPool, Balance[], CancellationToken, Task> payoutAction;
        private Func<CancellationToken, Task> lockAction;

        public TestEquihashPayoutHandler(IComponentContext ctx, IConnectionFactory cf,
            IMapper mapper, IShareRepository shareRepo, IBlockRepository blockRepo,
            IBalanceRepository balanceRepo, IPaymentRepository paymentRepo,
            IMasterClock clock, IMessageBus messageBus,
            IActiveBlockGracePeriodTracker activeBlockGracePeriodTracker) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo,
                clock, messageBus, activeBlockGracePeriodTracker)
        {
        }

        public void Configure(PoolConfig pool)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
            logger = LogManager.GetCurrentClassLogger();
        }

        public void SetPayoutAction(
            Func<IMiningPool, Balance[], CancellationToken, Task> action) =>
            payoutAction = action;

        public void SetLockAction(Func<CancellationToken, Task> action) =>
            lockAction = action;

        public void MarkWalletUnlocked() => MarkPayoutWalletUnlocked();

        public void QueueSuccess(Balance[] balances, string transactionId) =>
            NotifyPayoutSuccess(poolConfig.Id, balances,
                new[] { transactionId }, null);

        protected override Task PayoutTrackedAsync(IMiningPool pool,
            Balance[] balances, CancellationToken ct) =>
            payoutAction(pool, balances, ct);

        protected override Task LockWallet(CancellationToken ct) => lockAction(ct);
    }
}
