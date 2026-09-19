using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPublicationFailureTests
{
    [Fact]
    public async Task EarlierDisconnect_DoesNotHideIndependentCancellationFailure()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        wire.Connection.Disconnect();
        await wire.AssertDisconnectedAsync();
        wire.ClosePublicationFailure(new OperationCanceledException("independent publication operation"));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidProofBan_DoesNotConsumeLaterPublicationFailureReport(bool canonical)
    {
        var (config, manager, clock, bus) = Fixture();
        if(!canonical)
            config.Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"];
        config.Banning.CheckThreshold = 0;
        manager.ValidationFailure = new StratumException(StratumError.LowDifficultyShare, "invalid proof");
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: canonical);
        var bans = wire.EnableInvalidShareBanning();
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.IsAuthorized = context.IsSubscribed = true;
        context.AddJob(new TestJob(), 4);
        var responses = wire.Connection.ResponseSequence;
        var submission = new object[] { "miner.worker", "test-job", "00000000", "00000000", "00000000" };
        await wire.SendRawAsync(Request("mining.submit", submission) + "\n" + Request("mining.submit", submission));
        await wire.AssertDisconnectedAsync();
        Assert.True(wire.Connection.IsDisconnectRequested);
        Assert.Equal(1, manager.Submissions);
        Assert.Equal(1, context.Stats.InvalidShares);
        Assert.Equal(0, context.Stats.ValidShares);
        bans.Received(1).Ban(wire.Connection.RemoteEndpoint.Address, Arg.Any<TimeSpan>());
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Empty(context.validJobs);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
        wire.ClosePublicationFailure(new IOException("independent assignment failure"));
        wire.ClosePublicationFailure(new IOException("duplicate failure"));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryResponse_MiningFailStop_ClosesWithoutConsumingFailureReport(bool canonical)
    {
        var (config, manager, clock, bus) = Fixture();
        if(!canonical)
            config.Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: canonical);
        using var stop = new CancellationTokenSource();
        // No transport loop: force the precise SendAsync fail-stop gate rather
        // than allowing concurrent socket teardown to win this deterministic race.
        var connection = new StratumConnection(new NLog.NullLogger(NLog.LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(), clock, "recovery-fail-stop", false, stop.Token);
        var context = new BitcoinWorkerContext();
        context.Init(1, null, clock);
        context.AddJob(new TestJob(), 4);
        connection.SetContext(context);
        stop.Cancel();
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => wire.Reject(connection,
            new StratumException(StratumError.Other, "recoverable rejection"), stop.Token));
        Assert.Equal(stop.Token, failure.CancellationToken);
        Assert.Equal(1, connection.ResponseSequence);
        Assert.True(connection.IsDisconnectRequested);
        Assert.Empty(context.validJobs);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
        // Terminal cleanup still cannot turn another rejection into a report.
        await wire.Reject(connection, new StratumException(StratumError.Other, "closed"), stop.Token);
        Assert.Equal(1, connection.ResponseSequence);
        wire.ClosePublicationFailure(new IOException("independent publication failure"), connection);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AcceptedShare_ThrowingObserver_StillAcknowledgesAndKeepsSession(bool candidate, bool throwingLogger)
    {
        var (config, manager, clock, bus) = Fixture();
        manager.IsCandidate = candidate;
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        wire.Connection.Context.IsAuthorized = true;
        if(throwingLogger)
        {
            var logger = Substitute.For<NLog.ILogger>();
            logger.When(x => x.Info(Arg.Any<NLog.LogMessageGenerator>()))
                .Do(_ => throw new InvalidOperationException("logging failure"));
            wire.SetLogger(logger);
        }
        else
            bus.When(x => x.SendMessage(Arg.Is<TelemetryEvent>(e => e.Category == TelemetryCategory.Share), Arg.Any<string>()))
                .Do(_ => throw new InvalidOperationException("telemetry failure"));
        var response = await wire.RequestAsync("mining.submit");
        Assert.True(response["result"].Value<bool>());
        Assert.Equal(1, wire.Connection.Context.Stats.ValidShares);
        Assert.Equal(0, wire.Connection.Context.Stats.InvalidShares);
        Assert.Equal(candidate ? clock.Now : (DateTime?) null, wire.Canonical.LastPoolBlockTime);
        Assert.Equal(1, manager.Submissions);
        bus.Received(1).SendMessage(Arg.Any<Share>(), Arg.Any<string>());
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(e => e.Category == TelemetryCategory.Share), Arg.Any<string>());
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
        Assert.True((await wire.RequestAsync("mining.extranonce.subscribe"))["result"].Value<bool>());
        Assert.False(wire.Connection.IsDisconnectRequested);
    }

    [Fact]
    public async Task Broadcast_DisconnectBeforeJobCreation_DoesNotLogAnError()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        wire.Connection.Context.IsAuthorized = true;
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("broadcast-disconnect"));
        wire.Canonical.BeforeCreateJob = () => wire.Connection.Disconnect();
        await wire.AnnounceJobAsync(new object[] { "broadcast", false });
        await wire.AssertDisconnectedAsync();
        Assert.Empty(target.Logs);
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailure_PreservesOriginalExceptionAndDisconnects(bool missingContext)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        var original = new IOException("original publication failure");
        if(!missingContext)
            bus.When(x => x.SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>()))
                .Do(_ => throw new InvalidOperationException("telemetry cleanup failure"));
        wire.Canonical.AfterConfigure = () =>
        {
            if(missingContext)
                wire.Connection.SetContext<BitcoinWorkerContext>(null);
            throw original;
        };
        await wire.SendRawAsync(Request("mining.configure", Parameters("mining.configure")));
        await wire.AssertDisconnectedAsync();
        Assert.Same(original, wire.DispatchError);
        Assert.True(wire.Connection.IsDisconnectRequested);
        Assert.Equal(1, wire.Connection.ResponseSequence);
    }

    [Fact]
    public async Task BroadcastConstructingJob_DuringTerminalCleanup_CannotReinsertWork()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        wire.Connection.Context.IsAuthorized = true;
        var entered = Barrier();
        var release = Barrier();
        manager.BeforeGetJob = () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        };
        var jobs = wire.JobsCreated;
        var broadcast = wire.AnnounceJobAsync(new object[] { "broadcast", false });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            wire.ClosePublicationFailure(new IOException("concurrent publication failure"));
        }
        finally { release.TrySetResult(); }
        await broadcast.WaitAsync(Timeout);
        await wire.AssertDisconnectedAsync();
        Assert.Equal(jobs, wire.JobsCreated);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        Assert.Empty(context.validJobs);
        Assert.Throws<InvalidOperationException>(() => context.AddJob(new TestJob(), 4));
        await wire.AnnounceJobAsync(new object[] { "later", false });
        Assert.Empty(context.validJobs);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerEofWhileRequestInFlight_DistinguishesTeardownFromIndependentFailure(bool independentFailure)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        var entered = Barrier();
        var release = Barrier();
        var original = new IOException("independent handler failure");
        wire.Canonical.BeforeSubscribe = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Timeout);
            if(independentFailure)
                throw original;
        };
        await wire.SendRawAsync(Request("mining.subscribe"));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            wire.SendEof();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, wire.RequestCancellation).WaitAsync(Timeout));
        }
        finally { release.TrySetResult(); }
        await wire.AssertDisconnectedAsync();
        if(independentFailure)
            Assert.Same(original, wire.DispatchError);
        else
        {
            Assert.Null(wire.DispatchError);
            Assert.Equal(1, wire.Connection.ResponseSequence);
            Assert.Equal(StratumConnectionCompletionReason.PeerEof, wire.Connection.CompletionReason);
        }
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }
}
