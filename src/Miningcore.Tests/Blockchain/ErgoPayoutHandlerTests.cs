using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Ergo;
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

public class ErgoPayoutHandlerTests
{
    [Fact]
    public async Task PayoutAsync_SuccessThenLockFailureDoesNotReportPayoutFailure()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestErgoPayoutHandler(
            Substitute.For<IComponentContext>(), Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
            messageBus);
        var pool = new PoolConfig
        {
            Id = "ergo-test",
            Template = new ErgoCoinTemplate { Symbol = "ERG" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool);
        var balance = new Balance
        {
            PoolId = pool.Id,
            Address = "accepted",
            Amount = 1,
        };
        handler.SetPayoutAction((balances, _) =>
        {
            handler.QueueSuccess(balances, "tx-accepted");
            return Task.CompletedTask;
        });
        handler.SetLockAction(_ =>
            throw new InvalidOperationException("lock unavailable"));

        await handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { balance }, CancellationToken.None);

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

    private sealed class TestErgoPayoutHandler : ErgoPayoutHandler
    {
        private Func<Balance[], CancellationToken, Task> payoutAction;
        private Func<CancellationToken, Task> lockAction;

        public TestErgoPayoutHandler(IComponentContext ctx, IConnectionFactory cf,
            IMapper mapper, IShareRepository shareRepo, IBlockRepository blockRepo,
            IBalanceRepository balanceRepo, IPaymentRepository paymentRepo,
            IMasterClock clock, IMessageBus messageBus) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo,
                clock, messageBus)
        {
        }

        public void Configure(PoolConfig pool)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
            logger = LogManager.GetCurrentClassLogger();
        }

        public void SetPayoutAction(Func<Balance[], CancellationToken, Task> action) =>
            payoutAction = action;

        public void SetLockAction(Func<CancellationToken, Task> action) =>
            lockAction = action;

        public void QueueSuccess(Balance[] balances, string transactionId) =>
            NotifyPayoutSuccess(poolConfig.Id, balances,
                new[] { transactionId }, null);

        protected override Task PayoutTrackedAsync(Balance[] balances,
            IReadOnlyDictionary<string, decimal> amounts, decimal balancesTotal,
            CancellationToken ct) => payoutAction(balances, ct);

        protected override Task LockWallet(CancellationToken ct) => lockAction(ct);
    }
}
