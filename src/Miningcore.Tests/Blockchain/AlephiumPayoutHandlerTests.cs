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

    [Theory]
    [InlineData("Alephium address UTXO lookup")]
    [InlineData("Alephium transaction build")]
    [InlineData("Alephium transaction signing")]
    public async Task PreparedGroups_AcceptedThenPreparationFailure_FlushesSubsets(
        string operation)
    {
        var fixture = CreatePayoutFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-accepted", fixture.Now)
            .Returns(true);
        fixture.Handler.EnqueuePreparationResult();
        fixture.Handler.EnqueuePreparationException(
            new InvalidOperationException("preparation rejected"));
        fixture.Handler.EnqueueResult(new SubmitTxResult { TxId = "tx-accepted" });
        var accepted = Balance(fixture.Pool.Id, "accepted", 1);
        var failed = Balance(fixture.Pool.Id, "failed", 2);

        await fixture.Handler.RunPreparedGroupsAsync(operation,
            new[] { accepted }, new[] { failed });

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.Amount == 1 && x.RecipientsCount == 1),
            Arg.Any<string>());
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure &&
                x.Amount == 2 && x.RecipientsCount == 1 &&
                x.Error.Contains(operation) &&
                x.Error.Contains("preparation rejected")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task PreparedGroups_FailureThenUncertainSubmissionPreservesMembership()
    {
        var fixture = CreatePayoutFixture();
        fixture.Handler.EnqueuePreparationException(
            new InvalidOperationException("build rejected"));
        fixture.Handler.EnqueuePreparationResult();
        fixture.Handler.EnqueueException(new OperationCanceledException("response lost"));
        var failed = Balance(fixture.Pool.Id, "failed", 1);
        var uncertain = Balance(fixture.Pool.Id, "uncertain", 2);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.RunPreparedGroupsAsync("Alephium transaction build",
                new[] { failed }, new[] { uncertain }));

        Assert.Equal(failed.Address,
            Assert.Single(exception.Reconciliation.Failed).Address);
        Assert.Equal(uncertain.Address,
            Assert.Single(exception.Reconciliation.Uncertain).Address);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutAsync_UncertainSubmissionStillRelocksWithFreshToken()
    {
        var fixture = CreatePayoutFixture();
        var balance = Balance(fixture.Pool.Id, "uncertain", 1);
        var lockCalls = 0;
        var lockTokenWasCancelled = true;
        fixture.Handler.SetPayoutAction((_, _) =>
            throw new PayoutOutcomeUncertainException("wallet response lost"));
        fixture.Handler.SetLockAction(ct =>
        {
            lockCalls++;
            lockTokenWasCancelled = ct.IsCancellationRequested;
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { balance }, cts.Token));

        Assert.Equal(1, lockCalls);
        Assert.False(lockTokenWasCancelled);
    }

    [Fact]
    public async Task PayoutAsync_LockFailurePreservesUncertainOutcome()
    {
        var fixture = CreatePayoutFixture();
        var balance = Balance(fixture.Pool.Id, "uncertain", 1);
        fixture.Handler.SetPayoutAction((_, _) =>
            throw new PayoutOutcomeUncertainException("wallet response lost"));
        fixture.Handler.SetLockAction(_ =>
            throw new InvalidOperationException("lock unavailable"));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { balance }, CancellationToken.None));

        Assert.Contains("wallet response lost", exception.Message);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x =>
                x.Subject == "Payout wallet relock failed" &&
                x.Message.Contains("lock unavailable")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutAsync_SuccessThenLockFailureDoesNotReportPayoutFailure()
    {
        var fixture = CreatePayoutFixture();
        var balance = Balance(fixture.Pool.Id, "accepted", 1);
        fixture.Handler.SetPayoutAction((balances, _) =>
        {
            fixture.Handler.QueueSuccess(balances, "tx-accepted");
            return Task.CompletedTask;
        });
        fixture.Handler.SetLockAction(_ =>
            throw new InvalidOperationException("lock unavailable"));

        await fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { balance }, CancellationToken.None);

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success),
            Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure),
            Arg.Any<string>());
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Any<AdminNotification>(), Arg.Any<string>());
    }

    private static Balance Balance(string poolId, string address, decimal amount) =>
        new()
        {
            PoolId = poolId,
            Address = address,
            Amount = amount,
        };

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
        private readonly Queue<Func<Task<bool>>> preparations = new();
        private Func<Balance[], CancellationToken, Task> payoutAction;
        private Func<CancellationToken, Task> lockAction;

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

        public void EnqueuePreparationResult() =>
            preparations.Enqueue(() => Task.FromResult(true));

        public void EnqueuePreparationException(Exception exception) =>
            preparations.Enqueue(() => Task.FromException<bool>(exception));

        public void SetPayoutAction(Func<Balance[], CancellationToken, Task> action) =>
            payoutAction = action;

        public void SetLockAction(Func<CancellationToken, Task> action) =>
            lockAction = action;

        public void QueueSuccess(Balance[] balances, string transactionId) =>
            NotifyPayoutSuccess(poolConfig.Id, balances,
                new[] { transactionId }, null);

        public Task RunGroupsAsync(params Balance[][] groups) =>
            TrackPayoutAsync(groups.SelectMany(x => x).ToArray(), async () =>
            {
                foreach(var group in groups)
                    await SubmitPayoutGroupAsync(group, new SubmitSettlement(), 0,
                        CancellationToken.None);
            });

        public Task RunPreparedGroupsAsync(string operation,
            params Balance[][] groups) =>
            TrackPayoutAsync(groups.SelectMany(x => x).ToArray(), async () =>
            {
                foreach(var group in groups)
                {
                    var (success, _) = await TryPayoutPreparationAsync(group,
                        operation, preparations.Dequeue());
                    if(success)
                        await SubmitPayoutGroupAsync(group, new SubmitSettlement(), 0,
                            CancellationToken.None);
                }
            });

        protected override Task<SubmitTxResult> SubmitTransactionAsync(
            SubmitSettlement request, CancellationToken ct) => submissions.Dequeue()();

        protected override Task PayoutTrackedAsync(Balance[] balances,
            CancellationToken ct) => payoutAction != null
            ? payoutAction(balances, ct)
            : base.PayoutTrackedAsync(balances, ct);

        protected override Task LockWallet(CancellationToken ct) => lockAction != null
            ? lockAction(ct)
            : base.LockWallet(ct);
    }

    private sealed record PayoutFixture(TestAlephiumPayoutHandler Handler,
        PoolConfig Pool, IDbConnection Connection, IDbTransaction Transaction,
        IPaymentRepository PaymentRepo, IMessageBus MessageBus, DateTime Now);
}
