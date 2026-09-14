using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Util;

// Exercises both production persistence overloads without replacing OnRetry or backoff.
internal class PayoutPersistenceTestHandler(IConnectionFactory factory, IMapper mapper)
    : PayoutHandlerBase(factory, mapper, new ShareRepository(mapper), new BlockRepository(mapper),
        new BalanceRepository(mapper), new PaymentRepository(mapper), new StandardClock(), Substitute.For<IMessageBus>())
{
    protected override string LogCategory => "payout-persistence-test";
    public Task Persist(Balance[] balances, bool perRecipient = false, string transactionId = "tx-1")
    {
        logger = LogManager.GetCurrentClassLogger();
        poolConfig = new PoolConfig { Id = "ltc", Template = new BitcoinTemplate { Symbol = "LTC" }, RewardRecipients = Array.Empty<RewardRecipient>() };
        return perRecipient
            // Reconstruct the POCOs so reconciliation cannot rely on reference identity.
            ? PersistPaymentsAsync(balances.ToDictionary(balance => new Balance
                { PoolId = balance.PoolId, Address = balance.Address, Amount = balance.Amount }, _ => transactionId))
            : PersistPaymentsAsync(balances, transactionId);
    }

    public Task Pay(Balance[] balances, Func<Task<string>> submitWallet, bool perRecipient, string transactionId = "tx-1") =>
        TrackPayoutAsync(balances, async () =>
        {
            TrackPayoutSubmission(CancellationToken.None, balances);
            Assert.Equal(transactionId, await submitWallet());
            await Persist(balances, perRecipient, transactionId);
        });
}
