using System;
using System.Collections.Generic;
using System.Data;
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
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Kaspa;

public class KaspaPayoutHandlerTests
{
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
            logger = LogManager.GetCurrentClassLogger();
        }

        public Task RunMixedOutcomeAsync(Balance accepted, Balance failed,
            Balance uncertain, Balance untouched, Exception failure) =>
            TrackPayoutAsync(new[] { accepted, failed, uncertain, untouched },
                async () =>
                {
                    TrackPayoutSubmission(accepted);
                    await PersistPaymentsAsync(new[] { accepted }, "tx-accepted");
                    NotifyPayoutSuccess(poolConfig.Id, new[] { accepted },
                        new[] { "tx-accepted" }, null);
                    var failures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
                    RecordPreparationFailure(
                        new KeyValuePair<string, decimal>(failed.Address, failed.Amount),
                        failure, failures);
                    TrackPayoutSubmission(uncertain);
                    throw new PayoutOutcomeUncertainException(
                        "Kaspa broadcast response was lost");
                });
    }
}
