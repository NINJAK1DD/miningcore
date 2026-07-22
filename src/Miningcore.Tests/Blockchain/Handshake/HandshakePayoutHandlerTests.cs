using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Handshake;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Handshake;

public class HandshakePayoutHandlerTests
{
    [Fact]
    public async Task BrokenSendMany_DuplicateTransactionIdsAbortBeforePersistence()
    {
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var handler = new TestHandshakePayoutHandler(
            Substitute.For<IComponentContext>(),
            Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(),
            paymentRepo,
            Substitute.For<IMasterClock>(),
            Substitute.For<IMessageBus>());
        var poolConfig = new PoolConfig
        {
            Id = "hns-test",
            Template = new BitcoinTemplate
            {
                Symbol = "HNS",
                HasBrokenSendMany = true,
            },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(poolConfig);
        var balances = new[]
        {
            new Balance { PoolId = poolConfig.Id, Address = "hs1first", Amount = 1 },
            new Balance { PoolId = poolConfig.Id, Address = "hs1second", Amount = 2 },
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            handler.PayoutAsync(Substitute.For<IMiningPool>(), balances,
                CancellationToken.None));

        Assert.Contains("duplicate transaction id", exception.Message);
        Assert.Equal(2, exception.Reconciliation.Uncertain.Length);
        Assert.All(exception.Reconciliation.Uncertain,
            entry => Assert.Equal("duplicate-txid", entry.TransactionId));
        await paymentRepo.DidNotReceive().TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task BrokenSendMany_PreSubmissionCancellationRemainsConclusive()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var handler = CreateHandler(Substitute.For<IConnectionFactory>(),
            Substitute.For<IPaymentRepository>(), messageBus);
        using var cts = new CancellationTokenSource();
        handler.SetBeforeSubmission((_, _) => cts.Cancel());
        var balance = new Balance
        {
            PoolId = "hns-test",
            Address = "hs1cancelled",
            Amount = 1,
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.RunPayoutLoopAsync(new[] { balance }, cts.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(0, handler.SubmissionCalls);
        messageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task BrokenSendMany_CancellationAfterAcceptedRecipientPersistsAndFlushesSubset()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var handler = CreateHandler(cf, paymentRepo, messageBus);
        var now = DateTime.UtcNow;
        handler.SetClock(now);
        using var cts = new CancellationTokenSource();
        paymentRepo.TryBeginPaymentBatchAsync(connection, transaction, "hns-test",
                "tx-accepted", now)
            .Returns(true);
        handler.EnqueueSubmission((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(new RpcResponse<string>("tx-accepted"));
        });
        var balances = new[]
        {
            new Balance { PoolId = "hns-test", Address = "hs1accepted", Amount = 1 },
            new Balance { PoolId = "hns-test", Address = "hs1cancelled", Amount = 2 },
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.RunPayoutLoopAsync(balances, cts.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(1, handler.SubmissionCalls);
        await paymentRepo.Received(1).TryBeginPaymentBatchAsync(connection,
            transaction, "hns-test", "tx-accepted", now);
        messageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.Amount == 1 && x.RecipientsCount == 1),
            Arg.Any<string>());
        messageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Uncertain),
            Arg.Any<string>());
    }

    private static TestHandshakePayoutHandler CreateHandler(IConnectionFactory cf,
        IPaymentRepository paymentRepo, IMessageBus messageBus)
    {
        var handler = new TestHandshakePayoutHandler(
            Substitute.For<IComponentContext>(), cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(), paymentRepo,
            Substitute.For<IMasterClock>(), messageBus);
        handler.Configure(new PoolConfig
        {
            Id = "hns-test",
            Template = new BitcoinTemplate
            {
                Symbol = "HNS",
                HasBrokenSendMany = true,
            },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        });
        return handler;
    }

    private sealed class TestHandshakePayoutHandler : HandshakePayoutHandler
    {
        private readonly Queue<Func<object[], CancellationToken,
            Task<RpcResponse<string>>>> submissions = new();
        private Action<Balance, CancellationToken> beforeSubmission;

        public TestHandshakePayoutHandler(IComponentContext ctx,
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
            extraPoolConfig = new BitcoinPoolConfigExtra { HasBrokenSendMany = true };
            logger = LogManager.GetCurrentClassLogger();
        }

        public int SubmissionCalls { get; private set; }

        public void SetBeforeSubmission(Action<Balance, CancellationToken> action) =>
            beforeSubmission = action;

        public void EnqueueSubmission(Func<object[], CancellationToken,
            Task<RpcResponse<string>>> submission) => submissions.Enqueue(submission);

        public void SetClock(DateTime now) => clock.Now.Returns(now);

        public Task RunPayoutLoopAsync(Balance[] balances, CancellationToken ct) =>
            TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));

        protected override Task<RpcResponse<string>> SendToAddressAsync(object[] args,
            CancellationToken ct)
        {
            SubmissionCalls++;
            return submissions.Count > 0
                ? submissions.Dequeue()(args, ct)
                : Task.FromResult(new RpcResponse<string>("duplicate-txid"));
        }

        protected override void BeforePayoutSubmission(Balance balance,
            CancellationToken ct) => beforeSubmission?.Invoke(balance, ct);

        protected override int BrokenSendManyMaxDegreeOfParallelism => 1;
    }
}
