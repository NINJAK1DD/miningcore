using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Warthog;
using Miningcore.Blockchain.Warthog.DaemonRequests;
using Miningcore.Blockchain.Warthog.DaemonResponses;
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

public class WarthogPayoutHandlerTests
{
    [Fact]
    public async Task Payout_AddressValidationCancellationRemainsPreSubmission()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var handler = CreateHandler(messageBus: messageBus);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        handler.EnqueueAddressValidation((_, ct) =>
            Task.FromCanceled<WarthogBlockTemplate>(ct));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.RunPayoutLoopAsync(new[] { Balance("address-cancelled", 1m) },
                canceled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(0, handler.SubmissionCalls);
        messageBus.DidNotReceive().SendMessage(Arg.Any<PaymentNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_WalletBalanceCancellationRemainsPreSubmission()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var handler = CreateHandler(messageBus: messageBus);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        handler.EnqueueWalletBalance(ct =>
            Task.FromCanceled<WarthogBalance>(ct));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.RunPayoutLoopAsync(new[] { Balance("balance-cancelled", 1m) },
                canceled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(0, handler.SubmissionCalls);
        messageBus.DidNotReceive().SendMessage(Arg.Any<PaymentNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_CancellationBeforeLaterSubmissionPersistsKnownTransaction()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        paymentRepo.TryBeginPaymentBatchAsync(connection, transaction, "wart-test",
                "tx-first", Arg.Any<DateTime>())
            .Returns(true);
        var balanceRepo = Substitute.For<IBalanceRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var handler = CreateHandler(cf, balanceRepo, paymentRepo, messageBus);
        using var canceled = new CancellationTokenSource();
        handler.EnqueuePreparation(PreparedTransaction);
        handler.EnqueuePreparation(PreparedTransaction);
        handler.EnqueueSubmission((_, _) => Task.FromResult(
            new WarthogSendTransactionResponse
            {
                Data = new WarthogSendTransactionData { TxHash = "tx-first" },
            }));
        handler.SetBeforeSubmission((balance, _) =>
        {
            if(balance.Address == "later")
                canceled.Cancel();
        });
        var balances = new[] { Balance("first", 1m), Balance("later", 2m) };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.RunPayoutLoopAsync(balances, canceled.Token));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(1, handler.SubmissionCalls);
        await paymentRepo.Received(1).TryBeginPaymentBatchAsync(connection,
            transaction, "wart-test", "tx-first", Arg.Any<DateTime>());
        await balanceRepo.Received(1).AddAmountAsync(connection, transaction,
            "wart-test", "first", -1m, "Balance reset after payment");
        await balanceRepo.DidNotReceive().AddAmountAsync(connection, transaction,
            "wart-test", "later", Arg.Any<decimal>(), Arg.Any<string>());
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
    public async Task PayoutRecipient_ChainInfoCancellationRemainsPreSubmission()
    {
        var handler = CreateHandler();
        handler.EnqueuePreparation((_, _, _, _) =>
            throw new OperationCanceledException("chain-info cancelled"));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.RunRecipientsAsync(CancellationToken.None,
                ("chain-cancelled", 1m)));

        Assert.IsNotType<PayoutOutcomeUncertainException>(exception);
        Assert.Equal(0, handler.SubmissionCalls);
    }

    [Fact]
    public async Task PayoutRecipient_FeeTransportFailureIsConclusivePreparationFailure()
    {
        var handler = CreateHandler();
        handler.EnqueuePreparation((_, _, _, _) =>
            throw new HttpRequestException("fee endpoint unavailable"));

        var errors = await handler.RunRecipientsAsync(CancellationToken.None,
            ("fee-failed", 1m));

        var error = Assert.Single(errors);
        Assert.IsType<HttpRequestException>(error);
        Assert.Equal(0, handler.SubmissionCalls);
    }

    [Fact]
    public async Task PayoutRecipient_PostTransportFailureRemainsUncertain()
    {
        var handler = CreateHandler();
        handler.EnqueuePreparation(PreparedTransaction);
        handler.EnqueueSubmission((_, _) =>
            throw new HttpRequestException("POST response lost"));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            handler.RunRecipientsAsync(CancellationToken.None,
                ("post-uncertain", 1m)));

        Assert.Equal("post-uncertain",
            Assert.Single(exception.Reconciliation.Uncertain).Address);
        Assert.Equal(1, handler.SubmissionCalls);
    }

    [Fact]
    public async Task PayoutRecipient_BadRequestResponseIsConclusiveRejection()
    {
        var handler = CreateHandler();
        handler.EnqueuePreparation(PreparedTransaction);
        handler.EnqueueSubmission((_, _) =>
            throw new HttpRequestException("invalid transaction", null,
                HttpStatusCode.BadRequest));

        var errors = await handler.RunRecipientsAsync(CancellationToken.None,
            ("post-rejected", 1m));

        var error = Assert.Single(errors);
        var rejection = Assert.IsType<HttpRequestException>(error);
        Assert.Equal(HttpStatusCode.BadRequest, rejection.StatusCode);
        Assert.Equal(1, handler.SubmissionCalls);
    }

    [Fact]
    public async Task PayoutRecipients_PreflightFailureThenUncertainPostPreservesMembership()
    {
        var handler = CreateHandler();
        handler.EnqueuePreparation((_, _, _, _) =>
            throw new HttpRequestException("fee endpoint unavailable"));
        handler.EnqueuePreparation(PreparedTransaction);
        handler.EnqueueSubmission((_, _) =>
            throw new HttpRequestException("POST response lost"));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            handler.RunRecipientsAsync(CancellationToken.None,
                ("preflight-failed", 1m), ("post-uncertain", 2m)));

        var failed = Assert.Single(exception.Reconciliation.Failed);
        Assert.Equal("preflight-failed", failed.Address);
        Assert.Contains("fee endpoint unavailable", failed.Detail);
        Assert.Equal("post-uncertain",
            Assert.Single(exception.Reconciliation.Uncertain).Address);
        Assert.Equal(1, handler.SubmissionCalls);
    }

    private static Task<WarthogSendTransactionRequest> PreparedTransaction(
        string address, decimal amount, uint nonceId, CancellationToken ct) =>
        Task.FromResult(new WarthogSendTransactionRequest
        {
            ToAddress = address,
            NonceId = nonceId,
        });

    private static Balance Balance(string address, decimal amount) => new()
    {
        PoolId = "wart-test",
        Address = address,
        Amount = amount,
    };

    private static TestWarthogPayoutHandler CreateHandler(
        IConnectionFactory cf = null, IBalanceRepository balanceRepo = null,
        IPaymentRepository paymentRepo = null, IMessageBus messageBus = null)
    {
        var handler = new TestWarthogPayoutHandler(
            Substitute.For<IComponentContext>(), cf ?? Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), balanceRepo ?? Substitute.For<IBalanceRepository>(),
            paymentRepo ?? Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
            Substitute.For<IHttpClientFactory>(), messageBus ?? Substitute.For<IMessageBus>());
        handler.Configure(new PoolConfig
        {
            Id = "wart-test",
            Template = new WarthogCoinTemplate { Symbol = "WART" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        });
        return handler;
    }

    private sealed class TestWarthogPayoutHandler : WarthogPayoutHandler
    {
        private readonly Queue<Func<string, decimal, uint, CancellationToken,
            Task<WarthogSendTransactionRequest>>> preparations = new();
        private readonly Queue<Func<WarthogSendTransactionRequest,
            CancellationToken, Task<WarthogSendTransactionResponse>>> submissions = new();
        private readonly Queue<Func<string, CancellationToken,
            Task<WarthogBlockTemplate>>> addressValidations = new();
        private readonly Queue<Func<CancellationToken, Task<WarthogBalance>>>
            walletBalances = new();
        private Action<Balance, CancellationToken> beforeSubmission;

        public TestWarthogPayoutHandler(IComponentContext ctx, IConnectionFactory cf,
            IMapper mapper, IShareRepository shareRepo, IBlockRepository blockRepo,
            IBalanceRepository balanceRepo, IPaymentRepository paymentRepo,
            IMasterClock clock, IHttpClientFactory httpClientFactory,
            IMessageBus messageBus) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo,
                clock, httpClientFactory, messageBus)
        {
        }

        public int SubmissionCalls { get; private set; }

        public void Configure(PoolConfig pool)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
            logger = LogManager.GetCurrentClassLogger();
        }

        public void EnqueuePreparation(Func<string, decimal, uint,
            CancellationToken, Task<WarthogSendTransactionRequest>> preparation) =>
            preparations.Enqueue(preparation);

        public void EnqueueSubmission(Func<WarthogSendTransactionRequest,
            CancellationToken, Task<WarthogSendTransactionResponse>> submission) =>
            submissions.Enqueue(submission);

        public void EnqueueAddressValidation(Func<string, CancellationToken,
            Task<WarthogBlockTemplate>> validation) =>
            addressValidations.Enqueue(validation);

        public void EnqueueWalletBalance(Func<CancellationToken,
            Task<WarthogBalance>> balance) => walletBalances.Enqueue(balance);

        public void SetBeforeSubmission(Action<Balance, CancellationToken> action) =>
            beforeSubmission = action;

        public Task RunPayoutLoopAsync(Balance[] balances, CancellationToken ct) =>
            TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));

        public async Task<Exception[]> RunRecipientsAsync(CancellationToken ct,
            params (string Address, decimal Amount)[] recipients)
        {
            var balances = recipients.Select(x => new Balance
            {
                PoolId = poolConfig.Id,
                Address = x.Address,
                Amount = x.Amount,
            }).ToArray();
            var errors = new List<Exception>();

            await TrackPayoutAsync(balances, async () =>
            {
                for(var i = 0; i < recipients.Length; i++)
                {
                    var recipient = recipients[i];
                    var result = await PayoutRecipientAsync(recipient.Address,
                        recipient.Amount, (uint) (i + 1), ct);
                    if(result.Error != null)
                        errors.Add(result.Error);
                }
            });

            return errors.ToArray();
        }

        protected override Task<WarthogSendTransactionRequest>
            PreparePayoutTransactionAsync(string address, decimal amount,
                uint nonceId, CancellationToken ct) =>
            preparations.Dequeue()(address, amount, nonceId, ct);

        protected override Task<WarthogSendTransactionResponse>
            SendPayoutTransactionAsync(WarthogSendTransactionRequest request,
                CancellationToken ct)
        {
            SubmissionCalls++;
            return submissions.Dequeue()(request, ct);
        }

        protected override Task<WarthogBlockTemplate>
            GetPayoutAddressTemplateAsync(string address, CancellationToken ct) =>
            addressValidations.Count > 0
                ? addressValidations.Dequeue()(address, ct)
                : Task.FromResult(new WarthogBlockTemplate
                {
                    Data = new WarthogBlockTemplateData(),
                });

        protected override Task<WarthogBalance> GetPayoutWalletBalanceAsync(
            CancellationToken ct) =>
            walletBalances.Count > 0
                ? walletBalances.Dequeue()(ct)
                : Task.FromResult(new WarthogBalance
                {
                    Data = new WarthogBalanceData
                    {
                        Balance = (ulong) (100 * WarthogConstants.SmallestUnit),
                    },
                });

        protected override void BeforePayoutSubmission(Balance balance,
            CancellationToken ct) => beforeSubmission?.Invoke(balance, ct);

        protected override int PayoutMaxDegreeOfParallelism => 1;
    }
}
