using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Google.Protobuf;
using NLog;
using NSubstitute;
using Xunit;
using kaspaWalletd = Miningcore.Blockchain.Kaspa.KaspaWalletd;

namespace Miningcore.Tests.Blockchain.Kaspa;

public class KaspaPayoutHandlerTests
{
    [Fact]
    public async Task Payout_CancellationBeforeLaterBroadcastPersistsKnownTransaction()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        paymentRepo.TryBeginPaymentBatchAsync(connection, transaction, "kas-test",
                "tx-first", Arg.Any<DateTime>())
            .Returns(true);
        var balanceRepo = Substitute.For<IBalanceRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestKaspaPayoutHandler(
            Substitute.For<IComponentContext>(), cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            balanceRepo, paymentRepo, Substitute.For<IMasterClock>(), messageBus);
        handler.Configure(new PoolConfig
        {
            Id = "kas-test",
            Template = new KaspaCoinTemplate { Symbol = "KAS" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        });
        using var canceled = new CancellationTokenSource();
        handler.EnqueueBroadcast("tx-first");
        handler.SetBeforeSubmission((balance, _) =>
        {
            if(balance.Address == "later")
                canceled.Cancel();
        });
        var balances = new[]
        {
            new Balance
            {
                PoolId = "kas-test", Address = "first", Amount = 1m,
                Updated = DateTime.UtcNow.AddMinutes(-1),
            },
            new Balance
            {
                PoolId = "kas-test", Address = "later", Amount = 2m,
                Updated = DateTime.UtcNow,
            },
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.RunPayoutLoopAsync(balances, canceled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(1, handler.BroadcastCalls);
        await paymentRepo.Received(1).TryBeginPaymentBatchAsync(connection,
            transaction, "kas-test", "tx-first", Arg.Any<DateTime>());
        await balanceRepo.Received(1).AddAmountAsync(connection, transaction,
            "kas-test", "first", -1m, "Balance reset after payment");
        await balanceRepo.DidNotReceive().AddAmountAsync(connection, transaction,
            "kas-test", "later", Arg.Any<decimal>(), Arg.Any<string>());
        messageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.RecipientsCount == 1 && x.TxIds.Single() == "tx-first"),
            Arg.Any<string>());
        messageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Uncertain),
            Arg.Any<string>());
    }

    [Fact]
    public async Task MixedPerRecipientOutcome_PreservesEveryKnownState()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestKaspaPayoutHandler(
            Substitute.For<IComponentContext>(),
            cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(), paymentRepo, clock, messageBus);
        var pool = new PoolConfig
        {
            Id = "kas-test",
            Template = new KaspaCoinTemplate { Symbol = "KAS" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool);
        paymentRepo.TryBeginPaymentBatchAsync(connection, transaction, pool.Id,
                "tx-accepted", now)
            .Returns(true);
        var accepted = new Balance
        {
            PoolId = pool.Id,
            Address = "kaspa:accepted",
            Amount = 0.5m,
        };
        var failed = new Balance
        {
            PoolId = pool.Id,
            Address = "kaspa:failed",
            Amount = 1,
        };
        var uncertain = new Balance
        {
            PoolId = pool.Id,
            Address = "kaspa:uncertain",
            Amount = 2,
        };
        var untouched = new Balance
        {
            PoolId = pool.Id,
            Address = "kaspa:untouched",
            Amount = 3,
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            handler.RunMixedOutcomeAsync(accepted, failed, uncertain, untouched,
                new InvalidOperationException("unsigned transaction rejected")));

        var acceptedEntry = Assert.Single(exception.Reconciliation.Accepted);
        Assert.Equal(accepted.Address, acceptedEntry.Address);
        Assert.Equal("tx-accepted", acceptedEntry.TransactionId);
        var failedEntry = Assert.Single(exception.Reconciliation.Failed);
        Assert.Equal(failed.Address, failedEntry.Address);
        Assert.Contains("unsigned transaction rejected", failedEntry.Detail);
        Assert.Equal(uncertain.Address,
            Assert.Single(exception.Reconciliation.Uncertain).Address);
        Assert.Equal(untouched.Address,
            Assert.Single(exception.Reconciliation.NotAttempted).Address);
        messageBus.DidNotReceive().SendMessage(Arg.Any<PaymentNotification>(),
            Arg.Any<string>());
    }

    private sealed class TestKaspaPayoutHandler : KaspaPayoutHandler
    {
        private readonly Queue<string> broadcastTransactionIds = new();
        private Action<Balance, CancellationToken> beforeSubmission;

        public TestKaspaPayoutHandler(IComponentContext ctx, IConnectionFactory cf,
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
            network = "mainnet";
            logger = LogManager.GetCurrentClassLogger();
        }

        public int BroadcastCalls { get; private set; }

        public void EnqueueBroadcast(string transactionId) =>
            broadcastTransactionIds.Enqueue(transactionId);

        public void SetBeforeSubmission(Action<Balance, CancellationToken> action) =>
            beforeSubmission = action;

        public Task RunPayoutLoopAsync(Balance[] balances, CancellationToken ct) =>
            TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));

        public Task RunMixedOutcomeAsync(Balance accepted, Balance failed,
            Balance uncertain, Balance untouched, Exception failure) =>
            TrackPayoutAsync(new[] { accepted, failed, uncertain, untouched },
                async () =>
                {
                    TrackPayoutSubmission(CancellationToken.None, accepted);
                    await PersistPaymentsAsync(new[] { accepted }, "tx-accepted");
                    NotifyPayoutSuccess(poolConfig.Id, new[] { accepted },
                        new[] { "tx-accepted" }, null);
                    var failures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
                    RecordPreparationFailure(
                        new KeyValuePair<string, decimal>(failed.Address, failed.Amount),
                        failure, failures);
                    TrackPayoutSubmission(CancellationToken.None, uncertain);
                    throw new PayoutOutcomeUncertainException(
                        "Kaspa broadcast response was lost");
                });

        protected override Task<kaspaWalletd.GetBalanceResponse>
            GetPayoutWalletBalanceAsync(CancellationToken ct) =>
            Task.FromResult(new kaspaWalletd.GetBalanceResponse
            {
                Available = (ulong) (100 * KaspaConstants.SmallestUnit),
            });

        protected override Task<kaspaWalletd.CreateUnsignedTransactionsResponse>
            CreateUnsignedTransactionsAsync(
                kaspaWalletd.CreateUnsignedTransactionsRequest request,
                CancellationToken ct, Action<Exception> errorHandler)
        {
            var response = new kaspaWalletd.CreateUnsignedTransactionsResponse();
            response.UnsignedTransactions.Add(ByteString.CopyFromUtf8("unsigned"));
            return Task.FromResult(response);
        }

        protected override Task<kaspaWalletd.SignResponse> SignTransactionsAsync(
            kaspaWalletd.SignRequest request, CancellationToken ct,
            Action<Exception> errorHandler)
        {
            var response = new kaspaWalletd.SignResponse();
            response.SignedTransactions.Add(ByteString.CopyFromUtf8("signed"));
            return Task.FromResult(response);
        }

        protected override Task<kaspaWalletd.BroadcastResponse>
            BroadcastTransactionAsync(kaspaWalletd.BroadcastRequest request,
                CancellationToken ct, Action<Exception> errorHandler)
        {
            BroadcastCalls++;
            var response = new kaspaWalletd.BroadcastResponse();
            response.TxIDs.Add(broadcastTransactionIds.Dequeue());
            return Task.FromResult(response);
        }

        protected override void BeforePayoutSubmission(Balance balance,
            CancellationToken ct) => beforeSubmission?.Invoke(balance, ct);
    }
}
