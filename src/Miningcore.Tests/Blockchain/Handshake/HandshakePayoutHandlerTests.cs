using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Handshake;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Handshake.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
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
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Handshake;

public class HandshakePayoutHandlerTests
{
    [Theory]
    [InlineData(-500, "transport unavailable")]
    [InlineData(-4, "wallet service rejected request")]
    public async Task SendMany_GetWalletInfoFailureStopsBeforeSubmission(
        int errorCode, string errorMessage)
    {
        var fixture = CreateSendManyFixture();
        fixture.Handler.SetWalletInfoAction(_ =>
            Task.FromResult(new RpcResponse<WalletInfo>(null,
                new JsonRpcError(errorCode, errorMessage, null))));

        await fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { Balance(fixture.Pool.Id, "hs1preflight") },
            CancellationToken.None);

        await AssertConclusivePreflightFailure(fixture,
            HandshakeWalletCommands.GetWalletInfo);
    }

    [Fact]
    public async Task SendMany_EmptyWalletInfoStopsBeforeSubmission()
    {
        var fixture = CreateSendManyFixture();
        fixture.Handler.SetWalletInfoAction(_ =>
            Task.FromResult(new RpcResponse<WalletInfo>(null)));

        await fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { Balance(fixture.Pool.Id, "hs1emptywallet") },
            CancellationToken.None);

        await AssertConclusivePreflightFailure(fixture,
            HandshakeWalletCommands.GetWalletInfo);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Error.Contains("empty response")),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData(-500, "transport unavailable")]
    [InlineData(-4, "wallet selection rejected")]
    public async Task SendMany_SelectWalletFailureStopsBeforeSubmission(
        int errorCode, string errorMessage)
    {
        var fixture = CreateSendManyFixture();
        fixture.Handler.SetWalletInfoAction(_ =>
            Task.FromResult(new RpcResponse<WalletInfo>(new WalletInfo
            {
                WalletId = "other-wallet",
            })));
        fixture.Handler.SetSelectWalletAction((_, _) =>
            Task.FromResult(new RpcResponse<JToken>(null,
                new JsonRpcError(errorCode, errorMessage, null))));

        await fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { Balance(fixture.Pool.Id, "hs1selection") },
            CancellationToken.None);

        await AssertConclusivePreflightFailure(fixture,
            HandshakeWalletCommands.SelectWallet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendMany_PreflightCancellationRemainsOrdinaryShutdown(
        bool duringWalletSelection)
    {
        var fixture = CreateSendManyFixture();
        using var cancelled = new CancellationTokenSource();

        if(duringWalletSelection)
        {
            fixture.Handler.SetWalletInfoAction(_ =>
                Task.FromResult(new RpcResponse<WalletInfo>(new WalletInfo
                {
                    WalletId = "other-wallet",
                })));
            fixture.Handler.SetSelectWalletAction((_, _) =>
            {
                cancelled.Cancel();
                return Task.FromResult(new RpcResponse<JToken>(null,
                    new JsonRpcError(-500, "Cancelled", null)));
            });
        }
        else
        {
            fixture.Handler.SetWalletInfoAction(_ =>
            {
                cancelled.Cancel();
                return Task.FromResult(new RpcResponse<WalletInfo>(null,
                    new JsonRpcError(-500, "Cancelled", null)));
            });
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { Balance(fixture.Pool.Id, "hs1cancelled") },
                cancelled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(0, fixture.Handler.SendManyCalls);
        await AssertNothingPersisted(fixture);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SendMany_UnlockCancellationRelocksWithoutRetryOrFailure()
    {
        var fixture = CreateSendManyFixture();
        using var cancelled = new CancellationTokenSource();
        fixture.Handler.EnqueueSendMany(new RpcResponse<string>(null,
            new JsonRpcError(
                (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED,
                "wallet locked", null)));
        fixture.Handler.SetUnlockAction(_ =>
        {
            cancelled.Cancel();
            return Task.FromResult(new RpcResponse<JToken>(null,
                new JsonRpcError(-500, "Cancelled", null)));
        });
        CancellationToken relockToken = default;
        fixture.Handler.SetLockAction(ct =>
        {
            relockToken = ct;
            return Task.CompletedTask;
        });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { Balance(fixture.Pool.Id, "hs1unlockcancelled") },
                cancelled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(1, fixture.Handler.SendManyCalls);
        Assert.Equal(1, fixture.Handler.LockCalls);
        Assert.True(relockToken.CanBeCanceled);
        Assert.False(relockToken.IsCancellationRequested);
        await AssertNothingPersisted(fixture);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SendMany_SuccessPersistsBeforeRelockFailureAndRemainsSuccess()
    {
        var fixture = CreateSendManyFixture();
        var persisted = false;
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-accepted", fixture.Now)
            .Returns(_ =>
            {
                persisted = true;
                return true;
            });
        fixture.Handler.EnqueueSendMany(new RpcResponse<string>(null,
            new JsonRpcError(
                (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED,
                "wallet locked", null)));
        fixture.Handler.EnqueueSendMany(new RpcResponse<string>("tx-accepted"));
        fixture.Handler.SetLockAction(_ =>
        {
            Assert.True(persisted);
            return Task.FromException(
                new InvalidOperationException("lock unavailable"));
        });

        await fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
            new[] { Balance(fixture.Pool.Id, "hs1accepted") },
            CancellationToken.None);

        Assert.Equal(1, fixture.Handler.LockCalls);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Success &&
                x.TxIds.Single() == "tx-accepted"),
            Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure),
            Arg.Any<string>());
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x =>
                x.Subject == "Payout wallet relock failed" &&
                x.Message.Contains("lock unavailable")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task SendMany_UncertainSubmissionStillRelocksWithFreshToken()
    {
        var fixture = CreateSendManyFixture();
        using var cancelled = new CancellationTokenSource();
        fixture.Handler.EnqueueSendMany(new RpcResponse<string>(null,
            new JsonRpcError(
                (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED,
                "wallet locked", null)));
        fixture.Handler.EnqueueSendMany((_, _) =>
        {
            cancelled.Cancel();
            return Task.FromResult(new RpcResponse<string>(null,
                new JsonRpcError(-500, "response lost", null)));
        });
        CancellationToken relockToken = default;
        fixture.Handler.SetLockAction(ct =>
        {
            relockToken = ct;
            return Task.CompletedTask;
        });

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(Substitute.For<IMiningPool>(),
                new[] { Balance(fixture.Pool.Id, "hs1uncertain") },
                cancelled.Token));

        Assert.Contains("response lost", exception.ToString());
        Assert.Equal(1, fixture.Handler.LockCalls);
        Assert.True(relockToken.CanBeCanceled);
        Assert.False(relockToken.IsCancellationRequested);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

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

    private static SendManyFixture CreateSendManyFixture()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var handler = new TestHandshakePayoutHandler(
            Substitute.For<IComponentContext>(), cf, Substitute.For<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(), paymentRepo, clock, messageBus);
        var pool = new PoolConfig
        {
            Id = "hns-test",
            Template = new BitcoinTemplate
            {
                Symbol = "HNS",
                HasBrokenSendMany = false,
            },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        handler.Configure(pool, false);
        return new SendManyFixture(handler, pool, connection, transaction,
            paymentRepo, messageBus, now);
    }

    private static Balance Balance(string poolId, string address) => new()
    {
        PoolId = poolId,
        Address = address,
        Amount = 1,
    };

    private static async Task AssertConclusivePreflightFailure(
        SendManyFixture fixture, string operation)
    {
        Assert.Equal(0, fixture.Handler.SendManyCalls);
        await AssertNothingPersisted(fixture);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Failure &&
                x.Error.Contains(operation)),
            Arg.Any<string>());
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(x =>
                x.Outcome == PaymentNotificationOutcome.Uncertain),
            Arg.Any<string>());
    }

    private static async Task AssertNothingPersisted(SendManyFixture fixture)
    {
        await fixture.PaymentRepo.DidNotReceive().TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>());
    }

    private sealed class TestHandshakePayoutHandler : HandshakePayoutHandler
    {
        private readonly Queue<Func<object[], CancellationToken,
            Task<RpcResponse<string>>>> submissions = new();
        private readonly Queue<Func<object[], CancellationToken,
            Task<RpcResponse<string>>>> sendManyResponses = new();
        private Action<Balance, CancellationToken> beforeSubmission;
        private Func<CancellationToken, Task<RpcResponse<WalletInfo>>>
            walletInfoAction = _ => Task.FromResult(
                new RpcResponse<WalletInfo>(new WalletInfo
                {
                    WalletId = HandshakeConstants.WalletDefaultName,
                }));
        private Func<string, CancellationToken, Task<RpcResponse<JToken>>>
            selectWalletAction = (_, _) =>
                Task.FromResult(new RpcResponse<JToken>(null));
        private Func<CancellationToken, Task<RpcResponse<JToken>>>
            unlockAction = _ => Task.FromResult(new RpcResponse<JToken>(null));
        private Func<CancellationToken, Task> lockAction = _ => Task.CompletedTask;

        public TestHandshakePayoutHandler(IComponentContext ctx,
            IConnectionFactory cf, IMapper mapper, IShareRepository shareRepo,
            IBlockRepository blockRepo, IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo, IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo,
                clock, messageBus)
        {
        }

        public void Configure(PoolConfig pool, bool brokenSendMany = true)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
            extraPoolConfig = new BitcoinPoolConfigExtra
            {
                HasBrokenSendMany = brokenSendMany,
            };
            extraPoolPaymentProcessingConfig =
                new HandshakePoolPaymentProcessingConfigExtra
                {
                    WalletName = HandshakeConstants.WalletDefaultName,
                    WalletPassword = "test-password",
                };
            logger = LogManager.GetCurrentClassLogger();
        }

        public int SubmissionCalls { get; private set; }
        public int SendManyCalls { get; private set; }
        public int LockCalls { get; private set; }

        public void SetBeforeSubmission(Action<Balance, CancellationToken> action) =>
            beforeSubmission = action;

        public void EnqueueSubmission(Func<object[], CancellationToken,
            Task<RpcResponse<string>>> submission) => submissions.Enqueue(submission);

        public void EnqueueSendMany(RpcResponse<string> response) =>
            EnqueueSendMany((_, _) => Task.FromResult(response));

        public void EnqueueSendMany(Func<object[], CancellationToken,
            Task<RpcResponse<string>>> response) =>
            sendManyResponses.Enqueue(response);

        public void SetLockAction(Func<CancellationToken, Task> action) =>
            lockAction = action;

        public void SetWalletInfoAction(
            Func<CancellationToken, Task<RpcResponse<WalletInfo>>> action) =>
            walletInfoAction = action;

        public void SetSelectWalletAction(
            Func<string, CancellationToken, Task<RpcResponse<JToken>>> action) =>
            selectWalletAction = action;

        public void SetUnlockAction(
            Func<CancellationToken, Task<RpcResponse<JToken>>> action) =>
            unlockAction = action;

        public void SetClock(DateTime now) => clock.Now.Returns(now);

        public Task RunPayoutLoopAsync(Balance[] balances, CancellationToken ct) =>
            TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));

        protected override Task<RpcResponse<WalletInfo>> GetWalletInfoAsync(
            CancellationToken ct) => walletInfoAction(ct);

        protected override Task<RpcResponse<JToken>> SelectWalletAsync(
            string walletName, CancellationToken ct) =>
            selectWalletAction(walletName, ct);

        protected override Task<RpcResponse<string>> SendManyAsync(object[] args,
            CancellationToken ct)
        {
            SendManyCalls++;
            return sendManyResponses.Dequeue()(args, ct);
        }

        protected override Task<RpcResponse<JToken>> UnlockWalletAsync(
            CancellationToken ct) => unlockAction(ct);

        protected override Task LockWallet(CancellationToken ct)
        {
            LockCalls++;
            return lockAction(ct);
        }

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

    private sealed record SendManyFixture(TestHandshakePayoutHandler Handler,
        PoolConfig Pool, IDbConnection Connection, IDbTransaction Transaction,
        IPaymentRepository PaymentRepo, IMessageBus MessageBus, DateTime Now);
}
