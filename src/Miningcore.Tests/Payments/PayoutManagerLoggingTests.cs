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
            var manager = new PayoutManager(Substitute.For<IComponentContext>(), connections,
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
