using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NSubstitute;
using NLog;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutHandlerBaseTests
{
    [Fact]
    public async Task PersistPayments_DuplicateBatchDoesNotResetBalancesAgain()
    {
        var fixture = CreateFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-1", fixture.Now)
            .Returns(false);

        await fixture.Handler.PersistAsync(new[]
        {
            new Balance { PoolId = fixture.Pool.Id, Address = "miner", Amount = 12.5m },
        }, "tx-1");

        await fixture.PaymentRepo.DidNotReceive().InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>());
        await fixture.BalanceRepo.DidNotReceive().AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>());
        fixture.Transaction.Received(1).Commit();
    }

    [Fact]
    public async Task PersistPayments_NewBatchRecordsPaymentAndResetsBalanceAtomically()
    {
        var fixture = CreateFixture();
        fixture.PaymentRepo.TryBeginPaymentBatchAsync(fixture.Connection,
                fixture.Transaction, fixture.Pool.Id, "tx-1", fixture.Now)
            .Returns(true);
        var balance = new Balance
        {
            PoolId = fixture.Pool.Id,
            Address = "miner",
            Amount = 12.5m,
        };

        await fixture.Handler.PersistAsync(new[] { balance }, "tx-1");

        await fixture.PaymentRepo.Received(1).InsertAsync(fixture.Connection,
            fixture.Transaction, Arg.Is<Payment>(x =>
                x.Address == balance.Address &&
                x.Amount == balance.Amount &&
                x.TransactionConfirmationData == "tx-1"));
        await fixture.BalanceRepo.Received(1).AddAmountAsync(fixture.Connection,
            fixture.Transaction, fixture.Pool.Id, balance.Address, -balance.Amount,
            "Balance reset after payment");
        fixture.Transaction.Received(1).Commit();
    }

    private static Fixture CreateFixture()
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);

        var mapper = Substitute.For<IMapper>();
        var shareRepo = Substitute.For<IShareRepository>();
        var blockRepo = Substitute.For<IBlockRepository>();
        var balanceRepo = Substitute.For<IBalanceRepository>();
        var paymentRepo = Substitute.For<IPaymentRepository>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var messageBus = Substitute.For<IMessageBus>();
        var pool = new PoolConfig
        {
            Id = "ltc-test",
            Template = new BitcoinTemplate { Symbol = "LTC" },
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        var handler = new TestPayoutHandler(cf, mapper, shareRepo, blockRepo,
            balanceRepo, paymentRepo, clock, messageBus);
        handler.Configure(pool);

        return new Fixture(handler, pool, connection, transaction, balanceRepo,
            paymentRepo, now);
    }

    private sealed class TestPayoutHandler : PayoutHandlerBase
    {
        public TestPayoutHandler(IConnectionFactory cf, IMapper mapper,
            IShareRepository shareRepo, IBlockRepository blockRepo,
            IBalanceRepository balanceRepo, IPaymentRepository paymentRepo,
            IMasterClock clock, IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock,
                messageBus)
        {
            logger = LogManager.GetCurrentClassLogger();
        }

        protected override string LogCategory => "test";

        public void Configure(PoolConfig pool)
        {
            poolConfig = pool;
            clusterConfig = new ClusterConfig();
        }

        public Task PersistAsync(Balance[] balances, string transactionConfirmation) =>
            PersistPaymentsAsync(balances, transactionConfirmation);
    }

    private sealed record Fixture(TestPayoutHandler Handler, PoolConfig Pool,
        IDbConnection Connection, IDbTransaction Transaction,
        IBalanceRepository BalanceRepo, IPaymentRepository PaymentRepo,
        DateTime Now);
}
