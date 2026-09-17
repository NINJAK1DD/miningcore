using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NSubstitute;
using NLog;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutHandlerBaseTests
{
    [Fact]
    public async Task PersistPayments_DuplicateBatchDoesNotResetBalancesAgain()
    {
        var fixture = CreateFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-1", fixture.Now)
            .Returns(false);

        await fixture.Handler.PersistAsync(new[]
        {
            new Balance { PoolId = fixture.Pool.Id, Address = "miner", Amount = 12.5m },
        }, "tx-1");

        await fixture.PaymentRepo.DidNotReceive().InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>());
        await fixture.BalanceRepo.DidNotReceive().AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>());
        fixture.Transaction.Received(1).Commit();
    }

    [Fact]
    public async Task PersistPayments_NewBatchRecordsPaymentAndResetsBalanceAtomically()
    {
        var fixture = CreateFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-1", fixture.Now)
            .Returns(true);
        var balance = new Balance
        {
            PoolId = fixture.Pool.Id,
            Address = "miner",
            Amount = 12.5m,
        };

        await fixture.Handler.PersistAsync(new[] { balance }, "tx-1");

        await fixture.PaymentRepo.Received(1).InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == balance.Address &&
                x.Amount == balance.Amount &&
                x.TransactionConfirmationData == "tx-1"));
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction, fixture.Pool.Id, balance.Address, -balance.Amount,
            "Balance reset after payment");
        fixture.Transaction.Received(1).Commit();
    }

    [Fact]
    public async Task PersistPayments_ZeroPercentRecipientRemainsInPaymentHistory()
    {
        var fixture = CreateFixture();
        fixture.Pool.RewardRecipients = new[]
        {
            new RewardRecipient { Address = "zero-fee-miner", Percentage = 0 },
            new RewardRecipient { Address = "active-fee", Percentage = 1 },
        };
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-1", fixture.Now)
            .Returns(true);
        var zeroFeeMiner = Balance("zero-fee-miner", 12.5m);
        var activeFee = Balance("active-fee", 0.5m);

        await fixture.Handler.PersistAsync(new[] { zeroFeeMiner, activeFee }, "tx-1");

        await fixture.PaymentRepo.Received(1).InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == zeroFeeMiner.Address &&
                x.Amount == zeroFeeMiner.Amount));
        await fixture.PaymentRepo.DidNotReceive().InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == activeFee.Address));
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction, fixture.Pool.Id, zeroFeeMiner.Address,
            -zeroFeeMiner.Amount, "Balance reset after payment");
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction, fixture.Pool.Id, activeFee.Address,
            -activeFee.Amount, "Balance reset after payment");
    }

    [Fact]
    public async Task PersistPerRecipientPayments_ZeroPercentRecipientRemainsInHistory()
    {
        var fixture = CreateFixture();
        fixture.Pool.RewardRecipients = new[]
        {
            new RewardRecipient { Address = "zero-fee-miner", Percentage = 0 },
            new RewardRecipient { Address = "active-fee", Percentage = 1 },
        };
        var zeroFeeMiner = Balance("zero-fee-miner", 12.5m);
        var activeFee = Balance("active-fee", 0.5m);
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-miner", fixture.Now)
            .Returns(true);
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-fee", fixture.Now)
            .Returns(true);

        await fixture.Handler.PersistAsync(new Dictionary<Balance, string>
        {
            [zeroFeeMiner] = "tx-miner",
            [activeFee] = "tx-fee",
        });

        await fixture.PaymentRepo.Received(1).InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == zeroFeeMiner.Address &&
                x.TransactionConfirmationData == "tx-miner"));
        await fixture.PaymentRepo.DidNotReceive().InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == activeFee.Address));
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction, fixture.Pool.Id, zeroFeeMiner.Address,
            -zeroFeeMiner.Amount, "Balance reset after payment");
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction, fixture.Pool.Id, activeFee.Address,
            -activeFee.Amount, "Balance reset after payment");
    }

    [Fact]
    public async Task TrackPayout_MixedPagedOutcomePreservesEveryRecipientState()
    {
        var fixture = CreateFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-accepted", fixture.Now)
            .Returns(true);
        var accepted = Balance("accepted", 1);
        var failed = Balance("failed", 2);
        var uncertain = Balance("uncertain", 3);
        var notAttempted = Balance("not-attempted", 4);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunTrackedAsync(
                new[] { accepted, failed, uncertain, notAttempted }, async () =>
                {
                    fixture.Handler.StartSubmission(accepted);
                    await fixture.Handler.PersistAsync(new[] { accepted }, "tx-accepted");
                    fixture.Handler.Fail(failed, "wallet rejected");
                    fixture.Handler.StartSubmission(uncertain);
                    throw new PayoutOutcomeUncertainException("wallet response lost");
                }));

        var acceptedEntry = Assert.Single(exception.Reconciliation.Accepted);
        Assert.Equal("accepted", acceptedEntry.Address);
        Assert.Equal("tx-accepted", acceptedEntry.TransactionId);
        Assert.Equal("failed", Assert.Single(exception.Reconciliation.Failed).Address);
        Assert.Equal("uncertain", Assert.Single(exception.Reconciliation.Uncertain).Address);
        Assert.Equal("not-attempted",
            Assert.Single(exception.Reconciliation.NotAttempted).Address);
        Assert.Equal(10, exception.Reconciliation.Accepted.Sum(x => x.Amount) +
            exception.Reconciliation.Failed.Sum(x => x.Amount) +
            exception.Reconciliation.Uncertain.Sum(x => x.Amount) +
            exception.Reconciliation.NotAttempted.Sum(x => x.Amount));
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TrackPayout_ConclusiveNotificationsFlushOnlyAfterCompletion()
    {
        var fixture = CreateFixture();
        var failed = Balance("failed", 2);

        await fixture.Handler.RunTrackedAsync(new[] { failed }, () =>
        {
            fixture.Handler.Fail(failed, "wallet rejected");
            fixture.MessageBus.DidNotReceive().SendMessage(
                Arg.Any<PaymentNotification>(), Arg.Any<string>());
            return Task.CompletedTask;
        });

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure &&
                x.RecipientsCount == 1 &&
                x.Amount == 2),
            Arg.Any<string>());
    }

    [Fact]
    public async Task TrackPayout_CancellationWithInFlightSubmissionBecomesUncertain()
    {
        var fixture = CreateFixture();
        var balance = Balance("miner", 1);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { balance }, () =>
            {
                fixture.Handler.StartSubmission(balance);
                throw new OperationCanceledException("shutdown");
            }));

        Assert.Equal("miner", Assert.Single(exception.Reconciliation.Uncertain).Address);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task TrackPayout_PreSubmissionCancellationRemainsConclusive()
    {
        var fixture = CreateFixture();
        var balance = Balance("miner", 1);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { balance }, () =>
                throw new OperationCanceledException("shutdown")));
    }

    [Fact]
    public async Task TrackPayout_AlreadyCancelledSubmissionIsNotMarkedInFlight()
    {
        var fixture = CreateFixture();
        var balance = Balance("miner", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { balance }, () =>
            {
                fixture.Handler.StartSubmission(cts.Token, balance);
                return Task.CompletedTask;
            }));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TrackPayout_ConclusivePreSubmissionRejectionClearsInFlightState()
    {
        var fixture = CreateFixture();
        var balance = Balance("miner", 1);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { balance }, () =>
            {
                fixture.Handler.StartSubmission(balance);
                fixture.Handler.SubmissionNotStarted(balance);
                throw new OperationCanceledException("shutdown during wallet unlock");
            }));

        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TrackPayout_CancellationAfterPersistedSubsetFlushesSuccess()
    {
        var fixture = CreateFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-accepted", fixture.Now)
            .Returns(true);
        var accepted = Balance("accepted", 1);
        var untouched = Balance("untouched", 2);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { accepted, untouched }, async () =>
            {
                fixture.Handler.StartSubmission(accepted);
                await fixture.Handler.PersistAsync(new[] { accepted }, "tx-accepted");
                fixture.Handler.Succeed(accepted, "tx-accepted");
                throw new OperationCanceledException("shutdown");
            }));

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.RecipientsCount == 1 &&
                x.Amount == 1),
            Arg.Any<string>());
    }

    [Fact]
    public async Task TrackPayout_DuplicatePerRecipientTransactionIdsFailClosed()
    {
        var fixture = CreateFixture();
        var first = Balance("first", 1);
        var second = Balance("second", 2);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { first, second }, () =>
            {
                fixture.Handler.StartSubmission(first);
                fixture.Handler.RecordTransaction(new[] { first }, "duplicate-txid");
                fixture.Handler.StartSubmission(second);
                fixture.Handler.RecordTransaction(new[] { second }, "duplicate-txid");
                return Task.CompletedTask;
            }));

        Assert.Contains("duplicate transaction id", exception.Message);
        Assert.Equal(new[] { "first", "second" }, exception.Reconciliation.Uncertain
            .Select(x => x.Address).OrderBy(x => x));
        Assert.All(exception.Reconciliation.Uncertain,
            entry => Assert.Equal("duplicate-txid", entry.TransactionId));
    }

    [Fact]
    public async Task TrackPayout_DirectUncertaintyPreservesOriginatingException()
    {
        var fixture = CreateFixture();
        var balance = Balance("miner", 1);
        var original = new PayoutOutcomeUncertainException("wallet response lost");

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunTrackedAsync(new[] { balance }, () => throw original));

        Assert.Same(original, exception.InnerException);
        Assert.Equal("miner",
            Assert.Single(exception.Reconciliation.NotAttempted).Address);
    }

    private static Balance Balance(string address, decimal amount) => new()
    {
        PoolId = "ltc-test",
        Address = address,
        Amount = amount,
    };

    private static Fixture CreateFixture()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);

        var mapper = Substitute.For<IMapper>();
        var shareRepo = Substitute.For<IShareRepository>();
        var blockRepo = Substitute.For<IBlockRepository>();
        var balanceRepo = Substitute.For<IBalanceRepository>();
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var messageBus = Substitute.For<IMessageBus>();
        var pool = new PoolConfig
        {
            Id = "ltc-test",
            Template = new BitcoinTemplate { Symbol = "LTC" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        var handler = new TestPayoutHandler(cf, mapper, shareRepo, blockRepo,
            balanceRepo, paymentRepo, clock, messageBus);
        handler.Configure(pool);

        return new Fixture(handler, pool, connection, transaction, balanceRepo,
            paymentRepo, messageBus, now);
    }

    private sealed class TestPayoutHandler : PayoutHandlerBase
    {
        public TestPayoutHandler(IConnectionFactory cf, IMapper mapper,
            IShareRepository shareRepo, IBlockRepository blockRepo,
            IBalanceRepository balanceRepo, IPaymentRepository paymentRepo,
            IMasterClock clock, IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock,
                messageBus)
        {
            logger = LogManager.GetCurrentClassLogger();
        }

        protected override string LogCategory => "test";

        public void Configure(PoolConfig pool)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
        }

        public Task PersistAsync(Balance[] balances, string transactionConfirmation) =>
            PersistPaymentsAsync(balances, transactionConfirmation);

        public Task PersistAsync(Dictionary<Balance, string> balances) =>
            PersistPaymentsAsync(balances);

        public Task RunTrackedAsync(Balance[] balances, Func<Task> action) =>
            TrackPayoutAsync(balances, action);

        public void StartSubmission(params Balance[] balances) =>
            TrackPayoutSubmission(CancellationToken.None, balances);

        public void StartSubmission(CancellationToken ct, params Balance[] balances) =>
            TrackPayoutSubmission(ct, balances);

        public void SubmissionNotStarted(params Balance[] balances) =>
            TrackPayoutSubmissionNotStarted(balances);

        public void RecordTransaction(Balance[] balances, string transactionId) =>
            TrackPayoutTransaction(balances, transactionId);

        public void Fail(Balance balance, string detail) =>
            NotifyPayoutFailure(poolConfig.Id, new[] { balance }, detail, null);

        public void Succeed(Balance balance, string transactionId) =>
            NotifyPayoutSuccess(poolConfig.Id, new[] { balance },
                new[] { transactionId }, null);
    }

    private sealed record Fixture(TestPayoutHandler Handler, PoolConfig Pool,
        IDbConnection Connection, IDbTransaction Transaction,
        IBalanceRepository BalanceRepo, IPaymentRepository PaymentRepo,
        IMessageBus MessageBus, DateTime Now);
}
