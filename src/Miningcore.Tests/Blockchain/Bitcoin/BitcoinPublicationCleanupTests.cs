using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Time;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

// No sockets or deadline collection: these exercise synchronous cleanup gates.
public class BitcoinPublicationCleanupTests : TestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecoveryResponse_MiningFailStop_ClosesWithoutConsumingFailureReport(bool canonical,
        bool requestCancellationPropagated)
    {
        var config = new PoolConfig
        {
            Id = "cleanup-test", Coin = canonical ? "bitcoin" : "bitcoin-blake2b",
            Template = ModuleInitializer.CoinTemplates[canonical ? "bitcoin" : "bitcoin-blake2b"],
        };
        var clock = Substitute.For<IMasterClock>();
        var bus = Substitute.For<IMessageBus>();
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        var streams = container.Resolve<RecyclableMemoryStreamManager>();
        BitcoinBlake2bWireSession.IWirePool pool = canonical ?
            new BitcoinBlake2bWireSession.CanonicalPool(scope, clock, bus, streams) :
            new BitcoinBlake2bWireSession.TestPool(scope, clock, bus, streams, TimeProvider.System);
        pool.Configure(config, new ClusterConfig());
        using var stop = new CancellationTokenSource();
        // No transport loop: force the precise SendAsync fail-stop gate rather
        // than allowing concurrent socket teardown to win this deterministic race.
        var connection = new StratumConnection(new NLog.NullLogger(NLog.LogManager.LogFactory),
            streams, clock, "recovery-fail-stop", false, stop.Token);
        var context = new BitcoinWorkerContext();
        context.Init(1, null, clock);
        context.AddJob(new BitcoinJob(), 4);
        connection.SetContext(context);
        stop.Cancel();
        var requestToken = requestCancellationPropagated ? stop.Token : CancellationToken.None;
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => pool.Reject(connection,
            new StratumException(StratumError.Other, "recoverable rejection"), requestToken));
        Assert.Equal(stop.Token, failure.CancellationToken);
        Assert.Equal(1, connection.ResponseSequence);
        Assert.True(connection.IsDisconnectRequested);
        Assert.Empty(context.validJobs);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
        // Terminal cleanup still cannot turn another rejection into a report.
        await pool.Reject(connection, new StratumException(StratumError.Other, "closed"), stop.Token);
        Assert.Equal(1, connection.ResponseSequence);
        // An unrelated cancellation must still report even though fail-stop is active.
        pool.ClosePublicationFailure(connection, new OperationCanceledException("independent publication failure"));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Fact]
    public void ClearJobs_PreservesRegistryLifetime()
    {
        var context = new BitcoinWorkerContext();
        context.AddJob(new BitcoinJob(), 4);
        context.ClearJobs();
        Assert.Empty(context.validJobs);
        context.AddJob(new BitcoinJob(), 4);
        Assert.Single(context.validJobs);

        context.CloseJobs();
        context.ClearJobs();
        Assert.Throws<BitcoinJobRegistryClosedException>(() => context.AddJob(new BitcoinJob(), 4));
        // Closure takes precedence even when no payout authorization exists.
        Assert.Throws<BitcoinJobRegistryClosedException>(() => context.TryAddDirectJob(new BitcoinJob(), 4));
        Assert.Empty(context.validJobs);
    }
}
