using System;
using System.Threading;
using CircularBuffer;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Miningcore.VarDiff;
using NSubstitute;
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
    public void ZeroAverage_HonorsEffectiveMaximumAndMaxDelta(bool idle, bool limitDelta, bool explicitMaximum)
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
    public void ZeroAverage_UsesProportionalEstimateInsteadOfGenericMaximum(bool limitDelta)
    {
        var (context, options, clock) = Fixture();
        options.MaxDelta = limitDelta ? 2 : null;
        Assert.Equal(limitDelta ? 12d : 1000d, VarDiffManager.Update(context, options, clock));
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void BackwardClock_RebasesWithoutRetargetAndRecovers(bool idle, bool limitDelta, bool blake2b)
    {
        var (context, options, clock) = Fixture();
        options.MaxDelta = limitDelta ? 2 : null;
        var maximum = blake2b ? BitcoinBlake2bDifficulty.Maximum : double.MaxValue;
        var lastAssignment = context.VarDiff.LastUpdate = clock.Now.AddSeconds(-100);
        clock.CurrentTime = clock.Now.AddSeconds(-1);
        var result = idle ? VarDiffManager.IdleUpdate(context, options, clock, maximum) :
            VarDiffManager.Update(context, options, clock, maximum);
        Assert.Null(result);
        Assert.Equal(10, context.Difficulty);
        Assert.Equal(clock.Now.ToUnixSeconds(), context.VarDiff.LastTs);
        Assert.Equal(clock.Now.ToUnixSeconds(), context.VarDiff.LastRetarget);
        Assert.Null(context.VarDiff.TimeBuffer);
        Assert.Equal(lastAssignment, context.VarDiff.LastUpdate);
        clock.CurrentTime = clock.Now.AddSeconds(5);
        result = idle ? VarDiffManager.IdleUpdate(context, options, clock, maximum) :
            VarDiffManager.Update(context, options, clock, maximum);
        Assert.Equal(limitDelta ? 12d : 20d, result);
        Assert.Equal(clock.Now, context.VarDiff.LastUpdate);
    }

    [Fact]
    public void ShareAndIdleUpdates_ReadClockUnderTheSameStateLock()
    {
        var (context, options, mutableClock) = Fixture();
        context.VarDiff.TimeBuffer = null;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ =>
        {
            // A pre-lock read would allow this sample to become stale while
            // the idle producer advances LastTs. Check the invariant directly.
            Assert.True(Monitor.IsEntered(context.VarDiff));
            return mutableClock.Now;
        });
        mutableClock.CurrentTime = mutableClock.Now.AddSeconds(10);
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(1010, context.VarDiff.LastTs);
        Assert.Equal(900, context.VarDiff.LastRetarget);
        mutableClock.CurrentTime = mutableClock.Now.AddSeconds(1);
        Assert.Equal(100, VarDiffManager.Update(context, options, clock));
        Assert.Equal(1011, context.VarDiff.LastTs);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidHistory_IsDiscardedBeforeItCanContaminateAnotherAverage(double sample)
    {
        var (context, options, clock) = Fixture();
        context.VarDiff.TimeBuffer.PushBack(sample);
        Assert.Null(VarDiffManager.Update(context, options, clock));
        Assert.Null(context.VarDiff.TimeBuffer);
        Assert.Equal(clock.Now.ToUnixSeconds(), context.VarDiff.LastRetarget);
        clock.CurrentTime = clock.Now.AddSeconds(5);
        Assert.Equal(20, VarDiffManager.Update(context, options, clock));
    }

    [Fact]
    public void FutureRetargetTimestamp_IsRebasedEvenWhenLastSampleIsNotInTheFuture()
    {
        var (context, options, clock) = Fixture();
        context.VarDiff.LastRetarget = clock.Now.ToUnixSeconds() + 10;
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(clock.Now.ToUnixSeconds(), context.VarDiff.LastRetarget);
        Assert.Null(context.VarDiff.TimeBuffer);
    }

    [Theory]
    [InlineData(double.NaN, 10d, double.MaxValue)]
    [InlineData(double.PositiveInfinity, 10d, double.MaxValue)]
    [InlineData(double.NegativeInfinity, 10d, double.MaxValue)]
    [InlineData(10d, double.NaN, double.MaxValue)]
    [InlineData(10d, double.PositiveInfinity, double.MaxValue)]
    [InlineData(10d, 10d, double.NaN)]
    [InlineData(10d, 10d, double.PositiveInfinity)]
    public void InvalidArithmeticInputs_ProduceNoRetarget(double difficulty, double target, double maximum)
    {
        var (context, options, clock) = Fixture(difficulty);
        options.TargetTime = target;
        Assert.Null(VarDiffManager.Update(context, options, clock, maximum));
        Assert.Null(context.VarDiff.LastUpdate);
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock, maximum));
        Assert.Null(context.VarDiff.LastUpdate);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.001)]
    public void ZeroAverage_DoesNotLowerDifficultyForSubsecondTargets(double target)
    {
        var (context, options, clock) = Fixture();
        options.TargetTime = target;
        Assert.Null(VarDiffManager.Update(context, options, clock));
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(10, context.Difficulty);
    }
}
