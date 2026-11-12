using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

// The pinned xUnit scheduler isolates this collection from parallel collections
// while the test temporarily replaces process-wide NLog configuration.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PayoutManagerLoggingCollection
{
    public const string Name = "Payout manager diagnostic logging";
}

[Collection(PayoutManagerLoggingCollection.Name)]
public class PayoutManagerLoggingTests
{
    [Fact]
    public async Task CompletionFailure_DoesNotLogDatabaseOrWalletExceptionText()
    {
        const string secret = "private-wallet-or-database-secret";
        var previous = LogManager.Configuration;
        using var target = new MemoryTarget { Layout = "${level}|${message}|${exception:format=tostring}" };
        var logging = new LoggingConfiguration();
        logging.AddRuleForAllLevels(target, "Miningcore.Payments.PayoutManager");
        try
        {
            LogManager.Configuration = logging;
            var connections = Substitute.For<IConnectionFactory>();
            var connection = Substitute.For<System.Data.IDbConnection>();
            connections.OpenConnectionAsync().Returns(connection);
            var balances = Substitute.For<IBalanceRepository>();
            balances.GetPoolBalancesOverThresholdAsync(connection, "pool", Arg.Any<decimal>())
                .Returns(new[] { new Miningcore.Persistence.Model.Balance { Address = "miner", Amount = 1m } });
            var lease = Substitute.For<IPayoutManagerLease>();
            lease.TryBeginWalletPayoutAsync(Arg.Any<CancellationToken>(), Arg.Any<WalletPayoutBatch>()).Returns(true);
            lease.CompleteWalletPayoutAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException(secret)));
            using var manager = new PayoutManager(Substitute.For<ILifetimeScope>(), connections,
                Substitute.For<IBlockRepository>(), Substitute.For<IShareRepository>(), balances,
                new ClusterConfig { PaymentProcessing = new() }, Substitute.For<IMessageBus>(), lease, new ProcessStatus());
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => manager.PayoutPoolBalancesAsync(
                Substitute.For<IMiningPool>(), new PoolConfig { Id = "pool", PaymentProcessing = new(),
                    Template = ModuleInitializer.CoinTemplates["bitcoin"] }, Substitute.For<IPayoutHandler>(), CancellationToken.None));
            Assert.Contains(target.Logs, x => x.Contains("completion is unrecorded"));
            Assert.Contains(target.Logs, x => x.Contains("PayoutManager.CompleteConclusiveBatchAsync"));
            Assert.DoesNotContain(secret, string.Join("\n", target.Logs));
        }
        finally { LogManager.Configuration = previous; }
    }

    [Fact]
    public async Task ClosedWalletAdmission_ReportsSkipWithoutReadingBalancesOrCallingWallet()
    {
        var previous = LogManager.Configuration;
        using var target = new MemoryTarget { Layout = "${level}|${message}" };
        var logging = new LoggingConfiguration();
        logging.AddRuleForAllLevels(target, "Miningcore.Payments.PayoutManager");
        try
        {
            LogManager.Configuration = logging;
            var balances = Substitute.For<IBalanceRepository>();
            var connections = Substitute.For<IConnectionFactory>();
            var lease = Substitute.For<IPayoutManagerLease>();
            var manager = new PayoutManager(Substitute.For<ILifetimeScope>(), connections,
                Substitute.For<IBlockRepository>(), Substitute.For<IShareRepository>(), balances,
                new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig() },
                Substitute.For<IMessageBus>(), lease, new ProcessStatus());
            var pool = Substitute.For<IMiningPool, IIsolatedMiningPool>();
            ((IIsolatedMiningPool) pool).TryAcquireOperation().Returns((IDisposable) null);
            var handler = Substitute.For<IPayoutHandler>();

            await manager.PayoutPoolBalancesAsync(pool, new PoolConfig { Id = "isolated-test" },
                handler, CancellationToken.None);

            Assert.Equal("Warn|Skipping wallet payments for isolated pool isolated-test; balances and liabilities are retained",
                Assert.Single(target.Logs));
            Assert.Empty(connections.ReceivedCalls());
            Assert.Empty(balances.ReceivedCalls());
            Assert.Empty(handler.ReceivedCalls());
            Assert.Empty(lease.ReceivedCalls());
        }
        finally
        {
            LogManager.Configuration = previous;
        }
    }
}
