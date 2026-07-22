using System;
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

    private sealed class TestHandshakePayoutHandler : HandshakePayoutHandler
    {
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

        protected override Task<RpcResponse<string>> SendToAddressAsync(object[] args,
            CancellationToken ct) =>
            Task.FromResult(new RpcResponse<string>("duplicate-txid"));
    }
}
