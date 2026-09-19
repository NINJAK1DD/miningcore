using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Notifications.Messages;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BroadcastConstructingJob_DuringInvalidShareBan_OnlyLogsIndependentFailure(bool independentFailure)
    {
        var (config, manager, clock, bus) = Fixture();
        config.Banning.Enabled = true;
        config.Banning.CheckThreshold = 0;
        config.Banning.InvalidPercent = 1;
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var bans = wire.EnableInvalidShareBanning();
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.IsAuthorized = true;
        var jobs = wire.JobsCreated;
        var responses = wire.Connection.ResponseSequence;
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("blake2b-broadcast-ban"));
        var entered = Signal();
        var release = Signal();
        var builds = 0;
        manager.BeforeGetJob = () =>
        {
            Interlocked.Increment(ref builds);
            entered.TrySetResult();
            release.Task.WaitAsync(BarrierTimeout).GetAwaiter().GetResult();
            if(independentFailure)
                throw new IOException("independent construction failure");
        };
        var broadcast = wire.AnnounceJobAsync(new object[] { "broadcast", false });
        try
        {
            await entered.Task.WaitAsync(BarrierTimeout);
            // Exercise real job lookup, invalid-share policy and terminal cleanup
            // while the broadcast holds the assignment gate inside construction.
            await wire.SendRequestAsync("mining.submit", "miner.worker", "unknown-job",
                "0000000000000000", "00000000", "0000000000000000");
            await wire.AssertDisconnectedAsync();
            Assert.Equal(1, context.Stats.InvalidShares);
            Assert.Equal(0, context.Stats.ValidShares);
            Assert.Equal(responses, wire.Connection.ResponseSequence);
            bans.Received(1).Ban(wire.Connection.RemoteEndpoint.Address, Arg.Any<TimeSpan>());
            Assert.Empty(context.validJobs);
        }
        finally { release.TrySetResult(); }
        await broadcast.WaitAsync(BarrierTimeout);
        Assert.Equal(1, builds);
        Assert.Equal(jobs, wire.JobsCreated);
        Assert.Empty(context.validJobs);
        if(independentFailure)
            Assert.Contains(target.Logs, x => x.StartsWith("Error|") && x.Contains("PoolBase.ForEachMinerAsync"));
        else
            Assert.DoesNotContain(target.Logs, x => x.StartsWith("Error|") || x.StartsWith("Fatal|"));
        Assert.DoesNotContain(target.Logs, x => x.Contains("AssignmentPublicationFailure"));
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
        // Both paths release the gate; the terminal session cannot publish work.
        await wire.UpdateVarDiffAsync(2e-9).WaitAsync(BarrierTimeout);
        Assert.Equal(jobs, wire.JobsCreated);
        wire.ClosePublicationFailure(new IOException("later publication failure"));
        wire.ClosePublicationFailure(new IOException("duplicate failure"));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(e => e.Info == "publication-failure"), Arg.Any<string>());
    }
}
