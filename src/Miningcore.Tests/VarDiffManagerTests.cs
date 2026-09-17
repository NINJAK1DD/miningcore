using System;
using CircularBuffer;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Miningcore.VarDiff;
using Xunit;

namespace Miningcore.Tests;

public class VarDiffManagerTests
{
    private static (WorkerContextBase, VarDiffConfig, MockMasterClock) Fixture(double difficulty = 10)
    {
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1000) };
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        var context = new WorkerContextBase();
        context.Init(difficulty, options, clock);
        context.VarDiff.LastTs = clock.Now.ToUnixSeconds();
        context.VarDiff.LastRetarget = clock.Now.ToUnixSeconds() - 100;
        context.VarDiff.TimeBuffer = new CircularBuffer<double>(10);
        for(var i = 0; i < 10; i++)
            context.VarDiff.TimeBuffer.PushBack(0);
        return (context, options, clock);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ZeroAverage_UsesFiniteEffectiveMaximumAndHonorsMaxDelta(bool idle, bool limitDelta, bool explicitMaximum)
    {
        var (context, options, clock) = Fixture();
        options.MaxDelta = limitDelta ? 2 : null;
        options.MaxDiff = explicitMaximum ? 100 : null;
        var result = idle ? VarDiffManager.IdleUpdate(context, options, clock, 200) :
            VarDiffManager.Update(context, options, clock, 200);
        Assert.Equal(limitDelta ? 12d : explicitMaximum ? 100d : 200d, result);
        Assert.Equal(explicitMaximum ? 100d : (double?) null, options.MaxDiff);
        Assert.Equal(clock.Now, context.VarDiff.LastUpdate);
        Assert.Equal(0, context.VarDiff.TimeBuffer.Size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultGenericMaximum_RemainsFiniteAndDoesNotCancelSmallDelta(bool limitDelta)
    {
        var (context, options, clock) = Fixture();
        options.MaxDelta = limitDelta ? 2 : null;
        Assert.Equal(limitDelta ? 12d : double.MaxValue, VarDiffManager.Update(context, options, clock));
    }

    [Theory]
    [InlineData(1e300, 1e10, 1e20, 1e290)]
    [InlineData(1e-300, 1e-10, 1e-20, 1e-290)]
    [InlineData(1e-300, 1e-30, 1e-40, 1e-290)]
    [InlineData(1e-300, 1e-23, 1e-24, 1e-299)]
    [InlineData(1e100, 10, 1e-300, double.MaxValue)]
    public void ExtremeRatios_AvoidIntermediateOverflowAndUnderflow(double difficulty, double target,
        double average, double expected)
    {
        var (context, options, clock) = Fixture(difficulty);
        options.MinDiff = double.Epsilon;
        options.TargetTime = target;
        // The most recent interval is zero; ten stored intervals plus that
        // sample produce the desired positive mean without wall-clock rounding.
        context.VarDiff.TimeBuffer = new CircularBuffer<double>(10);
        for(var i = 0; i < 10; i++)
            context.VarDiff.TimeBuffer.PushBack(average * 1.1);
        var result = VarDiffManager.Update(context, options, clock);
        Assert.NotNull(result);
        Assert.True(double.IsFinite(result.Value));
        Assert.InRange(result.Value / expected, 0.99999999999999, 1.00000000000001);
    }

    [Theory]
    [InlineData(5, null, 20)]
    [InlineData(20, null, 5)]
    [InlineData(5, 2d, 12)]
    [InlineData(20, 2d, 8)]
    [InlineData(1000, null, 1)]
    public void OrdinaryIntervals_PreserveProportionalRetargetAndBounds(double interval, double? maxDelta, double expected)
    {
        var (context, options, clock) = Fixture();
        options.MaxDelta = maxDelta;
        context.VarDiff.TimeBuffer = null;
        context.VarDiff.LastTs = clock.Now.ToUnixSeconds() - interval;
        Assert.Equal(expected, VarDiffManager.Update(context, options, clock));
    }
}
