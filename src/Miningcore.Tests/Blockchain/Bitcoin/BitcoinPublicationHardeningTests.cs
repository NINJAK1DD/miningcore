using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPublicationFailureTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AssignmentConstructingJob_DuringInvalidProofBan_PreservesFailureReport(
        bool direct, bool idleVarDiff, bool independentFailure)
    {
        var (config, manager, clock, bus) = Fixture(direct);
        config.Banning.CheckThreshold = 0;
        manager.ValidationFailure = new StratumException(StratumError.LowDifficultyShare, "invalid proof");
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        var bans = wire.EnableInvalidShareBanning();
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.IsAuthorized = context.IsSubscribed = true;
        if(direct)
        {
            var miner = new KeyId(new byte[20]).GetAddress(Network.RegTest);
            context.SetDirectPayoutAuthorization(miner.ToString(), miner);
        }
        context.AddJob(new TestJob(), 4);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("assignment-ban"));

        var entered = Barrier();
        var release = Barrier();
        var builds = 0;
        var original = new IOException("independent construction failure");
        manager.BeforeGetJob = () =>
        {
            Interlocked.Increment(ref builds);
            entered.TrySetResult();
            release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
            if(independentFailure)
                throw original;
        };
        // Job construction is synchronous. Run that producer concurrently with
        // the real request dispatcher so closure occurs inside construction.
        var assignment = Task.Run(() => idleVarDiff ? wire.UpdateVarDiffAsync(2) :
            wire.AnnounceJobAsync(new object[] { "broadcast", false }));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            await wire.SendRawAsync(Request("mining.submit", new object[]
                { "miner.worker", "test-job", "00000000", "00000000", "00000000" }));
            await wire.AssertDisconnectedAsync();
            Assert.Equal(1, manager.Submissions);
            Assert.Equal(1, context.Stats.InvalidShares);
            Assert.Equal(0, context.Stats.ValidShares);
            Assert.Equal(0, wire.Connection.ResponseSequence);
            bans.Received(1).Ban(wire.Connection.RemoteEndpoint.Address, Arg.Any<TimeSpan>());
            Assert.Empty(context.validJobs);
            bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
        }
        finally { release.TrySetResult(); }

        if(idleVarDiff && independentFailure)
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => assignment.WaitAsync(Timeout)));
        else
            await assignment.WaitAsync(Timeout);
        Assert.Equal(1, builds); // A closed direct registry must not trigger a second coinbase build.
        Assert.Equal(0, wire.JobsCreated);
        Assert.Empty(context.validJobs);
        if(!independentFailure)
        {
            Assert.DoesNotContain(target.Logs, x => x.StartsWith("Error|") || x.StartsWith("Fatal|") ||
                x.Contains("AssignmentPublicationFailure"));
            bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
        }
        else
            bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());

        wire.ClosePublicationFailure(new IOException("later publication failure"));
        wire.ClosePublicationFailure(new IOException("duplicate failure"));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
    }

    [Fact]
    public async Task DirectAssignment_AuthorizationChange_RebuildsForCurrentDestination()
    {
        var (config, manager, clock, bus) = Fixture(direct: true);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.IsAuthorized = context.IsSubscribed = true;
        var minerA = new KeyId(new byte[20]).GetAddress(Network.RegTest);
        var minerB = new KeyId(Enumerable.Repeat((byte) 1, 20).ToArray()).GetAddress(Network.RegTest);
        context.SetDirectPayoutAuthorization(minerA.ToString(), minerA);
        var builds = 0;
        manager.BeforeGetJob = () =>
        {
            if(++builds == 1)
                context.SetDirectPayoutAuthorization(minerB.ToString(), minerB);
        };
        await wire.AnnounceJobAsync(new object[] { "broadcast", false });
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        var job = Assert.Single(context.validJobs);
        Assert.Equal(2, builds);
        Assert.Equal(minerB.ToString(), job.DirectPayoutAddress);
        Assert.Equal(context.GetDirectPayoutAuthorization().Generation, job.DirectPayoutGeneration);
        Assert.False(wire.Connection.IsDisconnectRequested);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
    }

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
        Assert.Throws<StratumConnectionClosedException>(() => context.AddJob(new TestJob(), 4));
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
