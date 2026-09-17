using System.Linq;
using System.Threading.Tasks;
using CircularBuffer;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.VarDiff;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
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
        var expected = limitDelta ? 2e-9 : explicitMaximum ? 3e-9 : BitcoinBlake2bDifficulty.Maximum;
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
