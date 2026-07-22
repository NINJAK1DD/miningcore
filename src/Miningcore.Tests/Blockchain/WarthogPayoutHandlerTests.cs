using System;
using System.Collections.Generic;
using System.Linq;
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

    private static TestWarthogPayoutHandler CreateHandler()
    {
        var handler = new TestWarthogPayoutHandler(
            Substitute.For<IComponentContext>(), Substitute.For<IConnectionFactory>(),
            Substitute.For<IMapper>(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
            Substitute.For<IHttpClientFactory>(), Substitute.For<IMessageBus>());
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
    }
}
