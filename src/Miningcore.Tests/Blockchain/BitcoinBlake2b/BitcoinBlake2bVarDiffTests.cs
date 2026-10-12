using System.Linq;
using System.Threading.Tasks;
using CircularBuffer;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.VarDiff;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Fact]
    public async Task NestedBlake2bBroadcast_ThroughProductionPipelineFaultsOnlyItsPoolAndReportsOnce()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var owner = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await using var peer = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, owner);
        await Subscribe(owner);
        await Subscribe(peer);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        owner.SetLogger(logs.GetLogger("nested-pipeline"));
        using var jobsSource = new System.Reactive.Subjects.Subject<object>();
        using var subscription = owner.SubscribeJobs(jobsSource);
        var jobs = owner.JobsCreated;
        var ownerJobs = owner.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray();
        var peerJobs = peer.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray();
        var responses = owner.Connection.ResponseSequence + peer.Connection.ResponseSequence;
        await owner.AssignOperationAsync(async () =>
        {
            jobsSource.OnNext(new object[] { "nested", false });
            for(var attempt = 0; !owner.MiningFaulted && attempt < 1000; attempt++)
                await Task.Delay(1);
            Assert.True(owner.MiningFaulted);
        }).WaitAsync(BarrierTimeout);
        Assert.Single(target.Logs.Where(x => x.Contains("BitcoinBlake2bPool.FaultPool")));
        jobsSource.OnNext(new object[] { "later", false });
        await owner.AnnounceJobAsync(new object[] { "later-direct", false });
        Assert.Equal(jobs, owner.JobsCreated);
        Assert.Equal(responses, owner.Connection.ResponseSequence + peer.Connection.ResponseSequence);
        Assert.Equal(ownerJobs, owner.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray());
        Assert.Equal(peerJobs, peer.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray());
        foreach(var wire in new[] { owner, peer })
        {
            Assert.False(wire.Connection.IsDisconnectRequested);
            Assert.Equal(1, wire.Connection.Context.AssignmentGate.CurrentCount);
        }
        Assert.Single(target.Logs.Where(x => x.Contains("BitcoinBlake2bPool.FaultPool")));
        await using var other = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(other);
        Assert.False(other.MiningFaulted);
        Assert.True(other.Connection.IsAlive);
    }

    [Fact]
    public async Task NestedBlake2bBroadcast_FailsBeforeIterationAndPreservesBothMinersWork()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var owner = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await using var peer = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, owner);
        await Subscribe(owner);
        await Subscribe(peer);
        foreach(var wire in new[] { owner, peer })
        {
            await wire.SendRequestAsync("mining.authorize", "test.worker", "x");
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        }
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        owner.SetLogger(logs.GetLogger("nested-blake2b-broadcast"));
        var ownerJobs = owner.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray();
        var peerJobs = peer.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray();
        var responses = owner.Connection.ResponseSequence + peer.Connection.ResponseSequence;
        var jobs = owner.JobsCreated;

        await owner.AssignOperationAsync(async () =>
        {
            var error = await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
                owner.AnnounceJobAsync(new object[] { "nested", false }));
            Assert.Contains("cannot nest", error.Message);
            Assert.Equal(jobs, owner.JobsCreated);
            Assert.Equal(responses, owner.Connection.ResponseSequence + peer.Connection.ResponseSequence);
            Assert.Equal(ownerJobs, owner.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray());
            Assert.Equal(peerJobs, peer.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray());
            Assert.False(owner.Connection.IsDisconnectRequested);
            Assert.False(peer.Connection.IsDisconnectRequested);
            Assert.Empty(target.Logs);
        }).WaitAsync(BarrierTimeout);

        await owner.AnnounceJobAsync(new object[] { "normal", false }).WaitAsync(BarrierTimeout);
        Assert.Equal(jobs + 2, owner.JobsCreated);
        foreach(var wire in new[] { owner, peer })
        {
            Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
            await Fence(wire);
            Assert.True(wire.Connection.IsAlive);
            Assert.Equal(1, wire.Connection.Context.AssignmentGate.CurrentCount);
        }
        Assert.False(owner.MiningFaulted);
        Assert.Empty(target.Logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcreteBlake2b_AuthorizationIdleAndBroadcastCompleteUnderContention(bool idleOwnsGate)
    {
        var (config, manager, clock, bus) = Fixture();
        var time = new ManualTimeProvider();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            varDiff: options, varDiffTimeProvider: time);
        await Subscribe(wire);
        await wire.SendRequestAsync("mining.authorize", "test.worker", "x");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await wire.RetargetVarDiffAsync(false);
        time.AdvanceMonotonic(System.TimeSpan.FromSeconds(5));
        var reached = Signal();
        var release = Signal();
        var bothWaiting = Signal();
        var waiters = 0;
        wire.AssignmentWaiting = () =>
        {
            if(System.Threading.Interlocked.Increment(ref waiters) == 2)
                bothWaiting.TrySetResult();
        };
        Task idle;
        if(idleOwnsGate)
        {
            wire.BeforeVarDiffPublication = async () =>
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(BarrierTimeout);
            };
            idle = wire.RetargetVarDiffAsync(true);
            await reached.Task.WaitAsync(BarrierTimeout);
        }
        else
        {
            await wire.Connection.Context.AssignmentGate.WaitAsync();
            idle = wire.RetargetVarDiffAsync(true);
            await idle.WaitAsync(BarrierTimeout);
        }
        Task authorize = null, broadcast = null;
        try
        {
            authorize = wire.DispatchBufferedAsync("mining.authorize", "test.worker", Password(4e-9));
            broadcast = wire.AnnounceJobAsync(new object[] { "ready", false });
            await bothWaiting.Task.WaitAsync(BarrierTimeout);
        }
        finally
        {
            if(idleOwnsGate) release.TrySetResult();
            else wire.Connection.Context.AssignmentGate.Release();
        }
        await Task.WhenAll(idle, authorize, broadcast).WaitAsync(BarrierTimeout);
        await wire.SendRequestAsync("mining.extranonce.subscribe"); // Third TCP request is the FIFO fence.
        JObject message;
        var announcedFixed = false;
        do
        {
            message = await wire.ReadAsync();
            if(message["method"]?.Value<string>() == "mining.set_difficulty")
                announcedFixed |= message["params"][0].Value<double>() == 4e-9;
        } while(message["id"]?.Type != JTokenType.Integer || message["id"].Value<int>() != 3);
        Assert.True(message["result"].Value<bool>());
        Assert.True(announcedFixed);
        Assert.Equal(4e-9, wire.Connection.Context.Difficulty);
        Assert.Null(wire.Connection.Context.VarDiff);
        Assert.False(wire.Connection.Context.HasPendingDifficulty);
        Assert.True(wire.Connection.IsAlive);
        Assert.Equal(1, wire.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FixedDifficultyBeforeVarDiffAcquiresGate_PreventsCalculationAndPublication(bool idle, bool authorize)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        var original = context.VarDiff = new VarDiffContext(new ManualTimeProvider()) { Config = options,
            LastShareTimestamp = -5 * System.TimeSpan.TicksPerSecond, LastRetargetTimestamp = -100 * System.TimeSpan.TicksPerSecond };
        var entered = Signal();
        var release = Signal();
        wire.BeforeAssignment = async () =>
        {
            wire.BeforeAssignment = null;
            entered.TrySetResult();
            await release.Task.WaitAsync(BarrierTimeout);
        };
        var retarget = wire.RetargetVarDiffAsync(idle);
        try
        {
            await entered.Task.WaitAsync(BarrierTimeout);
            Assert.Null(original.LastUpdate); // Calculation has not happened outside the gate.
            if(authorize)
                await wire.SendRequestAsync("mining.authorize", "test.worker", Password(4e-9));
            else
                await Send(wire, true, 4e-9);
            var response = await wire.ReadAsync();
            Assert.True((authorize ? response["result"] : response["result"]["minimum-difficulty"]).Value<bool>());
            await Assignment(wire, 4e-9);
            Assert.Null(context.VarDiff);
        }
        finally { release.TrySetResult(); }
        await retarget.WaitAsync(BarrierTimeout);
        await Fence(wire); // No stale difficulty or notify may precede this response.
        Assert.Null(original.LastUpdate);
        Assert.Equal(4e-9, context.Difficulty);
        Assert.Null(context.VarDiff);
        Assert.Equal(2, wire.JobsCreated);
        Assert.True(wire.Connection.IsAlive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CalculatedVarDiff_HoldsGateUntilPublicationBeforeFixedDifficulty(bool idle, bool authorize)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        var original = context.VarDiff = new VarDiffContext(new ManualTimeProvider()) { Config = options,
            LastShareTimestamp = -5 * System.TimeSpan.TicksPerSecond, LastRetargetTimestamp = -100 * System.TimeSpan.TicksPerSecond };
        var calculated = Signal();
        var release = Signal();
        var waiting = Signal();
        wire.BeforeVarDiffPublication = async () =>
        {
            calculated.TrySetResult();
            await release.Task.WaitAsync(BarrierTimeout);
        };
        wire.AssignmentWaiting = () => waiting.TrySetResult();
        var retarget = wire.RetargetVarDiffAsync(idle);
        try
        {
            await calculated.Task.WaitAsync(BarrierTimeout);
            Assert.NotNull(original.LastUpdate);
            if(authorize)
                await wire.SendRequestAsync("mining.authorize", "test.worker", Password(4e-9));
            else
                await Send(wire, true, 4e-9);
            await waiting.Task.WaitAsync(BarrierTimeout);
            Assert.Same(original, context.VarDiff);
            Assert.Equal(1e-9, context.Difficulty);
        }
        finally { release.TrySetResult(); }
        await retarget.WaitAsync(BarrierTimeout);
        await Assignment(wire, 2e-9);
        var response = await wire.ReadAsync();
        Assert.True((authorize ? response["result"] : response["result"]["minimum-difficulty"]).Value<bool>());
        await Assignment(wire, 4e-9);
        await wire.RetargetVarDiffAsync(idle);
        await Fence(wire);
        Assert.Equal(4e-9, context.Difficulty);
        Assert.Null(context.VarDiff);
        Assert.Equal(3, wire.JobsCreated);
        Assert.True(wire.Connection.IsAlive);
    }

    [Fact]
    public async Task NoOpIdleSweep_DoesNotPublishFalseFastShareAssignment()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        context.VarDiff = new VarDiffContext(new ManualTimeProvider()) { Config = options,
            LastShareTimestamp = -30 * System.TimeSpan.TicksPerSecond, LastRetargetTimestamp = -100 * System.TimeSpan.TicksPerSecond };
        await wire.RetargetVarDiffAsync(true);
        await wire.RetargetVarDiffAsync(false);
        await Fence(wire);
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.True(wire.Connection.IsAlive);
    }

    [Theory]
    [InlineData(false, -3600)]
    [InlineData(true, -3600)]
    [InlineData(false, 3600)]
    [InlineData(true, 3600)]
    public async Task ServerVarDiff_WallClockCorrectionsPreserveAssignmentAndMonotonicRetarget(bool idle, int wallStep)
    {
        var (config, manager, clock, bus) = Fixture();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            varDiff: options, varDiffTimeProvider: time);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var now = clock.Now;
        Assert.Same(time, context.VarDiff.TimeProvider); // Production OnConnect -> Init wiring.
        await wire.RetargetVarDiffAsync(false); // First real share baseline.
        clock.Now.Returns(now.AddSeconds(wallStep));
        await wire.RetargetVarDiffAsync(idle);
        await Fence(wire); // No assignment notification may precede this response.
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Null(context.VarDiff.LastUpdate);
        Assert.True(context.VarDiff.TimeBuffer == null || context.VarDiff.TimeBuffer.Size == 1);
        Assert.True(wire.Connection.IsAlive);
        // A share at the unchanged counter contributes a real zero sample.
        // Remove it so the following five-second interval is the same for both producers.
        context.VarDiff.TimeBuffer = null;
        time.AdvanceMonotonic(System.TimeSpan.FromSeconds(5));
        await wire.RetargetVarDiffAsync(idle);
        await Assignment(wire, 2e-9);
        Assert.Equal(now.AddSeconds(wallStep), context.VarDiff.LastUpdate);
        await Fence(wire);
        Assert.True(wire.Connection.IsAlive);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task ServerVarDiff_ZeroIntervalsPublishRepresentableWorkWithoutChangingConfiguration(
        bool idle, bool limitDelta, bool explicitMaximum)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var originalJobs = context.validJobs.ToArray();
        var options = new VarDiffConfig { MinDiff = 1e-9, MaxDiff = explicitMaximum ? 3e-9 : null,
            MaxDelta = limitDelta ? 1e-9 : null, TargetTime = 10, RetargetTime = idle ? 1e-7 : 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        context.VarDiff = new VarDiffContext(new ManualTimeProvider()) { Config = options, LastShareTimestamp = idle ? -1 : 0,
            LastRetargetTimestamp = -10 * System.TimeSpan.TicksPerSecond, TimeBuffer = new CircularBuffer<double>(10) };
        for(var i = 0; i < 10; i++)
            context.VarDiff.TimeBuffer.PushBack(0);
        await wire.RetargetVarDiffAsync(idle);
        var expected = limitDelta ? 2e-9 : explicitMaximum ? 3e-9 : 1e-4;
        Assert.InRange(context.Difficulty / expected, 0.99999999999999, 1.00000000000001);
        // The proportional result can differ by one ULP from a decimal literal;
        // announcements and immutable target binding must still match it exactly.
        await Assignment(wire, context.Difficulty);
        await Fence(wire);
        Assert.True(wire.Connection.IsAlive);
        Assert.Equal(context.Difficulty, BitcoinBlake2bDifficulty.Create(context.Difficulty).Difficulty);
        Assert.Equal(explicitMaximum ? 3e-9 : (double?) null, options.MaxDiff);
        Assert.Equal(limitDelta ? 1e-9 : (double?) null, options.MaxDelta);
        Assert.False(context.HasPendingDifficulty);
        Assert.All(originalJobs, job => Assert.Contains(job, context.validJobs));
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
        // Server retargeting consumes none of the connection's eight tokens.
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, true, (i + 4) / 1e9);
        await Refused(wire, true);
    }
}
