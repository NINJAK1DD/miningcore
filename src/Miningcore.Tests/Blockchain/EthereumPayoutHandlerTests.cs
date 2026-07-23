using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Configuration;
using Miningcore.Messaging;
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

namespace Miningcore.Tests.Blockchain;

public class EthereumPayoutHandlerTests
{
    [Fact]
    public async Task PayoutLoop_AlreadyCancelledDoesNotInvokeWalletSubmission()
    {
        var fixture = CreateFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Handler.RunPayoutLoopAsync(cts.Token,
                Balance(fixture.Pool.Id, "cancelled", 1)));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(0, fixture.Handler.SubmissionCalls);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutLoop_CancellationBetweenRecipientsFlushesAcceptedSubset()
    {
        var fixture = CreateFixture();
        var first = Balance(fixture.Pool.Id, "accepted", 1);
        var second = Balance(fixture.Pool.Id, "cancelled", 2);
        using var cts = new CancellationTokenSource();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "0xabc", fixture.Now)
            .Returns(true);
        fixture.Handler.EnqueueSubmission((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(new RpcResponse<string>("0xabc"));
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Handler.RunPayoutLoopAsync(cts.Token, first, second));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(1, fixture.Handler.SubmissionCalls);
        await fixture.PaymentRepo.Received(1).TryBeginPaymentBatchAsync(
            fixture.Connection, fixture.Transaction, fixture.Pool.Id, "0xabc",
            fixture.Now);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.Amount == first.Amount && x.RecipientsCount == 1),
            Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Uncertain),
            Arg.Any<string>());
    }

    private static Balance Balance(string poolId, string address, decimal amount) =>
        new()
        {
            PoolId = poolId,
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
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestEthereumPayoutHandler(
            Substitute.For<IComponentContext>(), cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(), paymentRepo, clock, messageBus);
        var pool = new PoolConfig
        {
            Id = "eth-test",
            Address = "0xpool",
            Template = new EthereumCoinTemplate
            {
                Symbol = "ETH",
                RpcMethodPrefix = "eth_",
            },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool);

        return new Fixture(handler, pool, connection, transaction, paymentRepo,
            messageBus, now);
    }

    private sealed class TestEthereumPayoutHandler : EthereumPayoutHandler
    {
        private readonly Queue<Func<object, CancellationToken,
            Task<RpcResponse<string>>>> submissions = new();

        public TestEthereumPayoutHandler(IComponentContext ctx,
            IConnectionFactory cf, IMapper mapper, IShareRepository shareRepo,
            IBlockRepository blockRepo, IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo, IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo,
                clock, messageBus)
        {
        }

        public int SubmissionCalls { get; private set; }

        public void Configure(PoolConfig pool)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
            coin = (EthereumCoinTemplate) pool.Template;
            logger = LogManager.GetCurrentClassLogger();
        }

        public void EnqueueSubmission(Func<object, CancellationToken,
            Task<RpcResponse<string>>> submission) => submissions.Enqueue(submission);

        public Task RunPayoutLoopAsync(CancellationToken ct,
            params Balance[] balances) =>
            TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));

        protected override Task<RpcResponse<string>> SubmitTransactionAsync(
            object request, CancellationToken ct)
        {
            SubmissionCalls++;
            return submissions.Dequeue()(request, ct);
        }
    }

    private sealed record Fixture(TestEthereumPayoutHandler Handler,
        PoolConfig Pool, IDbConnection Connection, IDbTransaction Transaction,
        IPaymentRepository PaymentRepo, IMessageBus MessageBus, DateTime Now);
}
