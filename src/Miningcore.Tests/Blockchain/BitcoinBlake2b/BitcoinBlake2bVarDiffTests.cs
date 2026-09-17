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
        var original = context.VarDiff = new VarDiffContext { Config = options,
            LastTs = clock.Now.ToUnixSeconds() - 5, LastRetarget = clock.Now.ToUnixSeconds() - 100 };
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
        var original = context.VarDiff = new VarDiffContext { Config = options,
            LastTs = clock.Now.ToUnixSeconds() - 5, LastRetarget = clock.Now.ToUnixSeconds() - 100 };
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
        context.VarDiff = new VarDiffContext { Config = options,
            LastTs = clock.Now.ToUnixSeconds() - 30, LastRetarget = clock.Now.ToUnixSeconds() - 100 };
        await wire.RetargetVarDiffAsync(true);
        await wire.RetargetVarDiffAsync(false);
        await Fence(wire);
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.True(wire.Connection.IsAlive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerVarDiff_BackwardClockPreservesAssignmentAndResumesNormally(bool idle)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var options = new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        var now = clock.Now;
        context.VarDiff = new VarDiffContext { Config = options, LastTs = now.ToUnixSeconds(),
            LastRetarget = now.ToUnixSeconds() - 100, TimeBuffer = new CircularBuffer<double>(10) };
        for(var i = 0; i < 10; i++)
            context.VarDiff.TimeBuffer.PushBack(0);
        clock.Now.Returns(now.AddSeconds(-1));
        await wire.RetargetVarDiffAsync(idle);
        await Fence(wire); // No assignment notification may precede this response.
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Null(context.VarDiff.TimeBuffer);
        Assert.True(wire.Connection.IsAlive);
        clock.Now.Returns(now.AddSeconds(4));
        await wire.RetargetVarDiffAsync(idle);
        await Assignment(wire, 2e-9);
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
            MaxDelta = limitDelta ? 1e-9 : null, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        context.VarDiff = new VarDiffContext { Config = options, LastTs = clock.Now.ToUnixSeconds(),
            LastRetarget = clock.Now.ToUnixSeconds() - 10, TimeBuffer = new CircularBuffer<double>(10) };
        for(var i = 0; i < 10; i++)
            context.VarDiff.TimeBuffer.PushBack(0);
        await wire.RetargetVarDiffAsync(idle);
        var expected = limitDelta ? 2e-9 : explicitMaximum ? 3e-9 : 1e-7;
        await Assignment(wire, expected);
        await Fence(wire);
        Assert.True(wire.Connection.IsAlive);
        Assert.Equal(expected, context.Difficulty);
        Assert.Equal(expected, BitcoinBlake2bDifficulty.Create(context.Difficulty).Difficulty);
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
