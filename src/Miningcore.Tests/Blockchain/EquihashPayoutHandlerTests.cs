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
    public async Task PayoutAsync_FailureRelocksWithFreshTokenWithoutMaskingOutcome()
    {
        var messageBus = Substitute.For<IMessageBus>();
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
        var payoutFailure = new InvalidOperationException("payout outcome");
        handler.SetPayoutAction((_, _, _) => Task.FromException(payoutFailure));
        CancellationToken relockToken = default;
        handler.SetLockAction(ct =>
        {
            relockToken = ct;
            return Task.FromException(new InvalidOperationException("lock unavailable"));
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { Balance(pool.Id) }, cancelled.Token));

        Assert.Same(payoutFailure, thrown);
        Assert.True(relockToken.CanBeCanceled);
        Assert.False(relockToken.IsCancellationRequested);
        messageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x =>
                x.Subject == "Payout wallet relock failed" &&
                x.Message.Contains("lock unavailable")),
            Arg.Any<string>());
        messageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
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

        protected override Task PayoutTrackedAsync(IMiningPool pool,
            Balance[] balances, CancellationToken ct) =>
            payoutAction(pool, balances, ct);

        protected override Task LockWallet(CancellationToken ct) => lockAction(ct);
    }
}
