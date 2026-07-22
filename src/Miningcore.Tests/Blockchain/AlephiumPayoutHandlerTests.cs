using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Configuration;
using Miningcore.Messaging;
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

public class AlephiumPayoutHandlerTests
{
    [Fact]
    public void ParseSweepResults_RejectsNullEnvelope()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(null));
    }

    [Fact]
    public void ParseSweepResults_RejectsNullCollection()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults
            {
                Results = null,
            }));
    }

    [Fact]
    public void ParseSweepResults_RejectsEmptyCollection()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults()));
    }

    [Fact]
    public void ParseSweepResults_RejectsNullEntry()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults
            {
                Results = new Collection<TransferResult> { null },
            }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSweepResults_RejectsBlankTransactionId(string txId)
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults
            {
                Results = new Collection<TransferResult>
                {
                    new() { TxId = txId },
                },
            }));
    }

    [Fact]
    public void ParseSweepResults_ReturnsEveryValidResult()
    {
        var first = new TransferResult { TxId = "tx-1", FromGroup = 0, ToGroup = 1 };
        var second = new TransferResult { TxId = "tx-2", FromGroup = 2, ToGroup = 3 };

        var result = AlephiumPayoutHandler.ParseSweepResults(new TransferResults
        {
            Results = new Collection<TransferResult> { first, second },
        });

        Assert.Equal(new[] { first, second }, result);
    }

    [Fact]
    public async Task SubmitGroups_AcceptedThenRejected_FlushesAccurateSubsets()
    {
        var fixture = CreatePayoutFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-accepted", fixture.Now)
            .Returns(true);
        fixture.Handler.EnqueueResult(new SubmitTxResult { TxId = "tx-accepted" });
        fixture.Handler.EnqueueException(new AlephiumApiException("rejected", 400,
            "group rejected", new Dictionary<string, IEnumerable<string>>(), null));
        var accepted = new Balance
        {
            PoolId = fixture.Pool.Id,
            Address = "accepted",
            Amount = 1,
        };
        var failed = new Balance
        {
            PoolId = fixture.Pool.Id,
            Address = "failed",
            Amount = 2,
        };

        await fixture.Handler.RunGroupsAsync(new[] { accepted }, new[] { failed });

        await fixture.PaymentRepo.Received(1).TryBeginPaymentBatchAsync(
            fixture.Connection, fixture.Transaction, fixture.Pool.Id,
            "tx-accepted", fixture.Now);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.Amount == 1 && x.RecipientsCount == 1),
            Arg.Any<string>());
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure &&
                x.Amount == 2 && x.RecipientsCount == 1 &&
                x.Error.Contains("group rejected")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task SubmitGroups_AcceptedThenCancelled_PreservesPagedReconciliation()
    {
        var fixture = CreatePayoutFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-accepted", fixture.Now)
            .Returns(true);
        fixture.Handler.EnqueueResult(new SubmitTxResult { TxId = "tx-accepted" });
        fixture.Handler.EnqueueException(new OperationCanceledException("shutdown"));
        var accepted = new Balance
        {
            PoolId = fixture.Pool.Id,
            Address = "accepted",
            Amount = 1,
        };
        var uncertain = new Balance
        {
            PoolId = fixture.Pool.Id,
            Address = "uncertain",
            Amount = 2,
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunGroupsAsync(new[] { accepted }, new[] { uncertain }));

        Assert.Equal(accepted.Address,
            Assert.Single(exception.Reconciliation.Accepted).Address);
        Assert.Equal(uncertain.Address,
            Assert.Single(exception.Reconciliation.Uncertain).Address);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    private static PayoutFixture CreatePayoutFixture()
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
        var handler = new TestAlephiumPayoutHandler(
            Substitute.For<IComponentContext>(), cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(), paymentRepo, clock, messageBus);
        var pool = new PoolConfig
        {
            Id = "alph-test",
            Template = new AlephiumCoinTemplate { Symbol = "ALPH" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool);

        return new PayoutFixture(handler, pool, connection, transaction,
            paymentRepo, messageBus, now);
    }

    private sealed class TestAlephiumPayoutHandler : AlephiumPayoutHandler
    {
        private readonly Queue<Func<Task<SubmitTxResult>>> submissions = new();

        public TestAlephiumPayoutHandler(IComponentContext ctx,
            IConnectionFactory cf, IMapper mapper, IShareRepository shareRepo,
            IBlockRepository blockRepo, IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo, IMasterClock clock,
            IMessageBus messageBus) :
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

        public void EnqueueResult(SubmitTxResult result) =>
            submissions.Enqueue(() => Task.FromResult(result));

        public void EnqueueException(Exception exception) =>
            submissions.Enqueue(() => Task.FromException<SubmitTxResult>(exception));

        public Task RunGroupsAsync(params Balance[][] groups) =>
            TrackPayoutAsync(groups.SelectMany(x => x).ToArray(), async () =>
            {
                foreach(var group in groups)
                    await SubmitPayoutGroupAsync(group, new SubmitSettlement(), 0,
                        CancellationToken.None);
            });

        protected override Task<SubmitTxResult> SubmitTransactionAsync(
            SubmitSettlement request, CancellationToken ct) => submissions.Dequeue()();
    }

    private sealed record PayoutFixture(TestAlephiumPayoutHandler Handler,
        PoolConfig Pool, IDbConnection Connection, IDbTransaction Transaction,
        IPaymentRepository PaymentRepo, IMessageBus MessageBus, DateTime Now);
}
