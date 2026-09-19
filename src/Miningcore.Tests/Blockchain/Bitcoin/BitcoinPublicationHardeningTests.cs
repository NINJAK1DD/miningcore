using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
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

    [Fact]
    public async Task BanThenPublicationFailure_StillClearsWorkAndReportsOnce()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        wire.Canonical.BanForInvalidShares(wire.Connection);
        Assert.True(wire.Connection.IsDisconnectRequested);
        Assert.NotEmpty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        var responses = wire.Connection.ResponseSequence;
        var error = new StratumException(StratumError.Other, "post-disconnect failure");
        // Exercise responseStarted == false with an already disconnected connection.
        await wire.Canonical.Reject(wire.Connection, error);
        wire.ClosePublicationFailure(error);
        await wire.AssertDisconnectedAsync();
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
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
