using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
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
    public static IEnumerable<object[]> MalformedBroadcastResponses()
    {
        yield return new object[] { null, Array.Empty<string>() };
        yield return new object[] { new[] { "tx-only" }, new[] { "tx-only" } };
        yield return new object[] { new[] { "tx-split", " " }, new[] { "tx-split", " " } };
        yield return new object[] { new[] { "tx-duplicate", "tx-duplicate" },
            new[] { "tx-duplicate", "tx-duplicate" } };
    }

    [Fact]
    public async Task Payout_MultiTransactionBroadcastPersistsCanonicalAndReportsAllIds()
    {
        var fixture = CreatePayoutFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-recipient", fixture.Now)
            .Returns(true);
        fixture.Handler.EnqueueUnsignedTransactions(2);
        fixture.Handler.EnqueueSignedTransactions(2);
        fixture.Handler.EnqueueBroadcast("tx-split", "tx-recipient");
        var balance = Balance("recipient", 1m);

        await fixture.Handler.RunPayoutLoopAsync(new[] { balance },
            CancellationToken.None);

        await fixture.PaymentRepo.Received(1).TryBeginPaymentBatchAsync(
            fixture.Connection, fixture.Transaction, fixture.Pool.Id,
            "tx-recipient", fixture.Now);
        await fixture.PaymentRepo.Received(1).InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == balance.Address &&
                x.TransactionConfirmationData == "tx-recipient"));
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.TxIds.SequenceEqual(new[] { "tx-split", "tx-recipient" }) &&
                x.RecipientTransactionChains.Length == 1 &&
                x.RecipientTransactionChains[0].Address == balance.Address &&
                x.RecipientTransactionChains[0].CanonicalTransactionId ==
                    "tx-recipient" &&
                x.RecipientTransactionChains[0].TransactionIds.SequenceEqual(
                    new[] { "tx-split", "tx-recipient" })),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_MultipleRecipientChainsExposeBoundaries()
    {
        var fixture = CreatePayoutFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-recipient-a", fixture.Now)
            .Returns(true);
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-recipient-b", fixture.Now)
            .Returns(true);
        fixture.Handler.EnqueueUnsignedTransactions(2);
        fixture.Handler.EnqueueSignedTransactions(2);
        fixture.Handler.EnqueueBroadcast("tx-split-a", "tx-recipient-a");
        fixture.Handler.EnqueueUnsignedTransactions(2);
        fixture.Handler.EnqueueSignedTransactions(2);
        fixture.Handler.EnqueueBroadcast("tx-split-b", "tx-recipient-b");
        var first = Balance("recipient-a", 1m, DateTime.UtcNow.AddMinutes(-1));
        var second = Balance("recipient-b", 2m, DateTime.UtcNow);

        await fixture.Handler.RunPayoutLoopAsync(new[] { first, second },
            CancellationToken.None);

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.TxIds.SequenceEqual(new[]
                {
                    "tx-split-a", "tx-recipient-a",
                    "tx-split-b", "tx-recipient-b",
                }) &&
                x.RecipientTransactionChains.Length == 2 &&
                x.RecipientTransactionChains.Single(chain =>
                    chain.Address == first.Address).TransactionIds.SequenceEqual(
                    new[] { "tx-split-a", "tx-recipient-a" }) &&
                x.RecipientTransactionChains.Single(chain =>
                    chain.Address == second.Address).TransactionIds.SequenceEqual(
                    new[] { "tx-split-b", "tx-recipient-b" })),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_OverlappingRecipientChainsFailClosedBeforePersistence()
    {
        var fixture = CreatePayoutFixture();
        fixture.Handler.EnqueueUnsignedTransactions(2);
        fixture.Handler.EnqueueSignedTransactions(2);
        fixture.Handler.EnqueueBroadcast("tx-shared", "tx-recipient-a");
        fixture.Handler.EnqueueUnsignedTransactions(2);
        fixture.Handler.EnqueueSignedTransactions(2);
        fixture.Handler.EnqueueBroadcast("tx-shared", "tx-recipient-b");
        var first = Balance("recipient-a", 1m, DateTime.UtcNow.AddMinutes(-1));
        var second = Balance("recipient-b", 2m, DateTime.UtcNow);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunPayoutLoopAsync(new[] { first, second },
                CancellationToken.None));

        Assert.Contains("duplicate transaction ids", exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "tx-shared", "tx-recipient-a" },
            exception.Reconciliation.Uncertain.Single(x =>
                x.Address == first.Address).TransactionIds);
        Assert.Equal(new[] { "tx-shared", "tx-recipient-b" },
            exception.Reconciliation.Uncertain.Single(x =>
                x.Address == second.Address).TransactionIds);
        await fixture.PaymentRepo.DidNotReceive().InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>());
        await fixture.BalanceRepo.DidNotReceive().AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Theory]
    [MemberData(nameof(MalformedBroadcastResponses))]
    public async Task Payout_MalformedBroadcastResponseFailsClosed(
        string[] returnedIds, string[] expectedKnownIds)
    {
        var fixture = CreatePayoutFixture();
        fixture.Handler.EnqueueUnsignedTransactions(2);
        fixture.Handler.EnqueueSignedTransactions(2);
        fixture.Handler.EnqueueBroadcastResponse(returnedIds == null
            ? null
            : BroadcastResponse(returnedIds));
        var balance = Balance("uncertain", 1m);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunPayoutLoopAsync(new[] { balance },
                CancellationToken.None));

        var uncertain = Assert.Single(exception.Reconciliation.Uncertain);
        Assert.Equal(expectedKnownIds, uncertain.TransactionIds);
        await fixture.PaymentRepo.DidNotReceive().InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("null-create", "no unsigned transactions")]
    [InlineData("empty-create", "no unsigned transactions")]
    [InlineData("null-sign", "no signed transactions")]
    [InlineData("empty-sign", "no signed transactions")]
    [InlineData("sign-count-mismatch", "signed transaction(s) for")]
    public async Task Payout_UnusablePreparationResponseIsConclusiveFailure(
        string scenario, string expectedError)
    {
        var fixture = CreatePayoutFixture();
        switch(scenario)
        {
            case "null-create":
                fixture.Handler.EnqueueUnsignedResponse(null);
                break;
            case "empty-create":
                fixture.Handler.EnqueueUnsignedResponse(
                    new kaspaWalletd.CreateUnsignedTransactionsResponse());
                break;
            case "null-sign":
                fixture.Handler.EnqueueUnsignedTransactions(1);
                fixture.Handler.EnqueueSignResponse(null);
                break;
            case "empty-sign":
                fixture.Handler.EnqueueUnsignedTransactions(1);
                fixture.Handler.EnqueueSignResponse(new kaspaWalletd.SignResponse());
                break;
            case "sign-count-mismatch":
                fixture.Handler.EnqueueUnsignedTransactions(2);
                fixture.Handler.EnqueueSignedTransactions(1);
                break;
        }
        var balance = Balance("failed", 1m);

        await fixture.Handler.RunPayoutLoopAsync(new[] { balance },
            CancellationToken.None);

        Assert.Equal(0, fixture.Handler.BroadcastCalls);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure &&
                x.Error.Contains(expectedError)), Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Uncertain),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_PreparationFailureThenUncertainBroadcastPreservesMembership()
    {
        var fixture = CreatePayoutFixture();
        fixture.Handler.EnqueueUnsignedResponse(null);
        fixture.Handler.EnqueueBroadcastException(
            new HttpRequestException("broadcast response lost"));
        var now = DateTime.UtcNow;
        var failed = Balance("failed", 1m, now.AddMinutes(-1));
        var uncertain = Balance("uncertain", 2m, now);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunPayoutLoopAsync(new[] { failed, uncertain },
                CancellationToken.None));

        Assert.Equal(failed.Address,
            Assert.Single(exception.Reconciliation.Failed).Address);
        Assert.Equal(uncertain.Address,
            Assert.Single(exception.Reconciliation.Uncertain).Address);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_CancellationBeforeLaterBroadcastPersistsKnownTransaction()
    {
        var fixture = CreatePayoutFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, "kas-test",
                "tx-first", Arg.Any<DateTime>())
            .Returns(true);
        using var canceled = new CancellationTokenSource();
        fixture.Handler.EnqueueBroadcast("tx-first");
        fixture.Handler.SetBeforeSubmission((balance, _) =>
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
            fixture.Handler.RunPayoutLoopAsync(balances, canceled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(1, fixture.Handler.BroadcastCalls);
        await fixture.PaymentRepo.Received(1).TryBeginPaymentBatchAsync(
            fixture.Connection, fixture.Transaction, "kas-test", "tx-first",
            Arg.Any<DateTime>());
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction,
            "kas-test", "first", -1m, "Balance reset after payment");
        await fixture.BalanceRepo.DidNotReceive().AddAmountAsync(fixture.Connection,
            fixture.Transaction,
            "kas-test", "later", Arg.Any<decimal>(), Arg.Any<string>());
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.RecipientsCount == 1 && x.TxIds.Single() == "tx-first"),
            Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
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
        private readonly Queue<kaspaWalletd.CreateUnsignedTransactionsResponse>
            unsignedResponses = new();
        private readonly Queue<kaspaWalletd.SignResponse> signResponses = new();
        private readonly Queue<Func<Task<kaspaWalletd.BroadcastResponse>>>
            broadcastResponses = new();
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

        public void EnqueueUnsignedResponse(
            kaspaWalletd.CreateUnsignedTransactionsResponse response) =>
            unsignedResponses.Enqueue(response);

        public void EnqueueUnsignedTransactions(int count) =>
            unsignedResponses.Enqueue(UnsignedResponse(count));

        public void EnqueueSignResponse(kaspaWalletd.SignResponse response) =>
            signResponses.Enqueue(response);

        public void EnqueueSignedTransactions(int count) =>
            signResponses.Enqueue(SignedResponse(count));

        public void EnqueueBroadcast(params string[] transactionIds) =>
            EnqueueBroadcastResponse(BroadcastResponse(transactionIds));

        public void EnqueueBroadcastResponse(
            kaspaWalletd.BroadcastResponse response) =>
            broadcastResponses.Enqueue(() => Task.FromResult(response));

        public void EnqueueBroadcastException(Exception exception) =>
            broadcastResponses.Enqueue(() =>
                Task.FromException<kaspaWalletd.BroadcastResponse>(exception));

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
                CancellationToken ct) => Task.FromResult(unsignedResponses.Count > 0
                ? unsignedResponses.Dequeue()
                : UnsignedResponse(1));

        protected override Task<kaspaWalletd.SignResponse> SignTransactionsAsync(
            kaspaWalletd.SignRequest request, CancellationToken ct) =>
            Task.FromResult(signResponses.Count > 0
                ? signResponses.Dequeue()
                : SignedResponse(request.UnsignedTransactions.Count));

        protected override Task<kaspaWalletd.BroadcastResponse>
            BroadcastTransactionAsync(kaspaWalletd.BroadcastRequest request,
                CancellationToken ct)
        {
            BroadcastCalls++;
            return broadcastResponses.Count > 0
                ? broadcastResponses.Dequeue()()
                : Task.FromResult(BroadcastResponse(
                    Enumerable.Range(1, request.Transactions.Count)
                        .Select(x => $"tx-{x}").ToArray()));
        }

        protected override void BeforePayoutSubmission(Balance balance,
            CancellationToken ct) => beforeSubmission?.Invoke(balance, ct);
    }

    private static Balance Balance(string address, decimal amount,
        DateTime? updated = null) => new()
    {
        PoolId = "kas-test",
        Address = address,
        Amount = amount,
        Updated = updated ?? DateTime.UtcNow,
    };

    private static kaspaWalletd.CreateUnsignedTransactionsResponse UnsignedResponse(
        int count)
    {
        var response = new kaspaWalletd.CreateUnsignedTransactionsResponse();
        for(var i = 0; i < count; i++)
            response.UnsignedTransactions.Add(ByteString.CopyFromUtf8($"unsigned-{i}"));
        return response;
    }

    private static kaspaWalletd.SignResponse SignedResponse(int count)
    {
        var response = new kaspaWalletd.SignResponse();
        for(var i = 0; i < count; i++)
            response.SignedTransactions.Add(ByteString.CopyFromUtf8($"signed-{i}"));
        return response;
    }

    private static kaspaWalletd.BroadcastResponse BroadcastResponse(
        params string[] transactionIds)
    {
        var response = new kaspaWalletd.BroadcastResponse();
        response.TxIDs.Add(transactionIds);
        return response;
    }

    private static PayoutFixture CreatePayoutFixture()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var balanceRepo = Substitute.For<IBalanceRepository>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestKaspaPayoutHandler(
            Substitute.For<IComponentContext>(), cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            balanceRepo, paymentRepo, clock, messageBus);
        var pool = new PoolConfig
        {
            Id = "kas-test",
            Template = new KaspaCoinTemplate
            {
                Symbol = "KAS",
                ExplorerTxLink = "https://explorer.test/{0}",
            },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool);
        return new PayoutFixture(handler, pool, connection, transaction,
            paymentRepo, balanceRepo, messageBus, now);
    }

    private sealed record PayoutFixture(TestKaspaPayoutHandler Handler,
        PoolConfig Pool, IDbConnection Connection, IDbTransaction Transaction,
        IPaymentRepository PaymentRepo, IBalanceRepository BalanceRepo,
        IMessageBus MessageBus, DateTime Now);
}
