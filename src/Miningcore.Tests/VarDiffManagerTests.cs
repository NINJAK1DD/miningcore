using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using CircularBuffer;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Miningcore.VarDiff;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests;

public class VarDiffManagerTests
{
    public static IEnumerable<object[]> BurstWindows =>
        from stored in new[] { 0, 1, 4, 9, 10 }
        from ticks in new[] { 0, 1, 200 } // zero, 100 ns, 20 us
        from idle in new[] { false, true }
        select new object[] { stored, ticks, idle };

    [Theory]
    [MemberData(nameof(BurstWindows))]
    public void CoalescedBurst_FloorsZeroAndTinyPositiveMeansByAvailableSampleCount(int stored, int ticks, bool idle)
    {
        var (context, options, clock, time) = Fixture();
        options.RetargetTime = 1e-7;
        var currentTicks = idle ? Math.Max(1, ticks) : ticks;
        context.VarDiff.LastShareTimestamp = time.GetTimestamp() - currentTicks;
        context.VarDiff.TimeBuffer = new CircularBuffer<double>(10);
        for(var i = 0; i < stored; i++)
            context.VarDiff.TimeBuffer.PushBack(ticks / (double) TimeSpan.TicksPerSecond);
        var expected = 10 * 10 / (0.001 / Math.Min(stored + 1, 10));
        Assert.Equal(expected, idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock));
    }

    [Theory]
    [MemberData(nameof(BurstWindows))]
    public void BurstFloor_CannotInventADownwardRetargetForTinyTargets(int stored, int ticks, bool idle)
    {
        var (context, options, clock, time) = Fixture();
        options.RetargetTime = 1e-7;
        options.TargetTime = 0.00001;
        context.VarDiff.LastShareTimestamp = time.GetTimestamp() - (idle ? Math.Max(1, ticks) : ticks);
        context.VarDiff.TimeBuffer = new CircularBuffer<double>(10);
        for(var i = 0; i < stored; i++)
            context.VarDiff.TimeBuffer.PushBack(ticks / (double) TimeSpan.TicksPerSecond);
        Assert.Null(idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock));
        Assert.Null(context.VarDiff.LastUpdate);
        // Measuring a burst must not replace its positive observations with the floor.
        Assert.All(context.VarDiff.TimeBuffer, x => Assert.InRange(x, 0, 0.00002));
    }

    [Fact]
    public void IdleSweep_RequiresFullInactivityEvenAfterTheRetargetCooldownExpired()
    {
        var (context, options, clock, time) = Fixture();
        options.RetargetTime = 90;
        context.VarDiff.TimeBuffer = null;
        context.VarDiff.LastShareTimestamp = time.GetTimestamp();
        time.AdvanceMonotonic(TimeSpan.FromSeconds(89.6));
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(0.4));
        Assert.Equal(10 * 10 / 90d, VarDiffManager.IdleUpdate(context, options, clock));
    }
    private static (WorkerContextBase, VarDiffConfig, MockMasterClock, ManualTimeProvider) Fixture(double difficulty = 10,
        bool idleBurst = false)
    {
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1000) };
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        var context = new WorkerContextBase();
        var time = new ManualTimeProvider();
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1000));
        context.Init(difficulty, options, clock, time);
        // A real idle retarget must have an elapsed interval, even for a burst.
        if(idleBurst)
            options.RetargetTime = 1e-7;
        context.VarDiff.LastShareTimestamp = time.GetTimestamp() - (idleBurst ? 1 : 0);
        context.VarDiff.LastRetargetTimestamp = time.GetTimestamp() - 100 * TimeSpan.TicksPerSecond;
        context.VarDiff.TimeBuffer = new CircularBuffer<double>(10);
        for(var i = 0; i < 10; i++)
            context.VarDiff.TimeBuffer.PushBack(0);
        return (context, options, clock, time);
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
        var (context, options, clock, time) = Fixture(idleBurst: idle);
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
        var (context, options, clock, time) = Fixture();
        options.MaxDelta = limitDelta ? 2 : null;
        Assert.Equal(limitDelta ? 12d : 1000000d, VarDiffManager.Update(context, options, clock));
    }

    [Theory]
    [InlineData(1e300, 1e10, 1e20, 1e290)]
    [InlineData(1e-300, 1e-10, 0.001, 1e-307)]
    [InlineData(1e-300, 1e-30, 1, double.Epsilon)]
    [InlineData(1e306, 10, 0.001, double.MaxValue)]
    public void ExtremeRatios_AvoidIntermediateOverflowAndUnderflow(double difficulty, double target,
        double average, double expected)
    {
        var (context, options, clock, time) = Fixture(difficulty);
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
        var (context, options, clock, time) = Fixture();
        options.MaxDelta = maxDelta;
        context.VarDiff.TimeBuffer = null;
        context.VarDiff.LastShareTimestamp = time.GetTimestamp() - (long) (interval * TimeSpan.TicksPerSecond);
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
    public void BrokenMonotonicProvider_RebasesWithoutRetargetAndRecovers(bool idle, bool limitDelta, bool blake2b)
    {
        var (context, options, clock, time) = Fixture();
        options.MaxDelta = limitDelta ? 2 : null;
        var maximum = blake2b ? BitcoinBlake2bDifficulty.Maximum : double.MaxValue;
        var lastAssignment = context.VarDiff.LastUpdate = clock.Now.AddSeconds(-100);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(-1));
        clock.CurrentTime = clock.Now.AddSeconds(-1);
        var result = idle ? VarDiffManager.IdleUpdate(context, options, clock, maximum) :
            VarDiffManager.Update(context, options, clock, maximum);
        Assert.Null(result);
        Assert.Equal(10, context.Difficulty);
        Assert.Equal(time.GetTimestamp(), context.VarDiff.LastShareTimestamp);
        Assert.Equal(time.GetTimestamp(), context.VarDiff.LastRetargetTimestamp);
        Assert.Null(context.VarDiff.TimeBuffer);
        Assert.Equal(lastAssignment, context.VarDiff.LastUpdate);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        clock.CurrentTime = clock.Now.AddSeconds(5);
        result = idle ? VarDiffManager.IdleUpdate(context, options, clock, maximum) :
            VarDiffManager.Update(context, options, clock, maximum);
        Assert.Equal(limitDelta ? 12d : 20d, result);
        Assert.Equal(clock.Now, context.VarDiff.LastUpdate);
    }

    [Fact]
    public void AssignmentWallMarker_IsReadUnderTheStateLock()
    {
        var (context, options, mutableClock, time) = Fixture();
        context.VarDiff.TimeBuffer = null;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ =>
        {
            // Wall time is sampled only for a changed assignment, under the
            // same state lock as its monotonic retarget marker.
            Assert.True(Monitor.IsEntered(context.VarDiff));
            return mutableClock.Now;
        });
        time.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        mutableClock.CurrentTime = mutableClock.Now.AddSeconds(10);
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(1000 * TimeSpan.TicksPerSecond, context.VarDiff.LastShareTimestamp);
        Assert.Equal(900 * TimeSpan.TicksPerSecond, context.VarDiff.LastRetargetTimestamp);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        mutableClock.CurrentTime = mutableClock.Now.AddSeconds(1);
        Assert.Equal(100d / 11d, VarDiffManager.Update(context, options, clock));
        Assert.Equal(1011 * TimeSpan.TicksPerSecond, context.VarDiff.LastShareTimestamp);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidHistory_IsDiscardedBeforeItCanContaminateAnotherAverage(double sample)
    {
        var (context, options, clock, time) = Fixture();
        context.VarDiff.TimeBuffer.PushBack(sample);
        Assert.Null(VarDiffManager.Update(context, options, clock));
        Assert.Null(context.VarDiff.TimeBuffer);
        Assert.Equal(time.GetTimestamp(), context.VarDiff.LastRetargetTimestamp);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        clock.CurrentTime = clock.Now.AddSeconds(5);
        Assert.Equal(20, VarDiffManager.Update(context, options, clock));
    }

    [Fact]
    public void FutureRetargetTimestamp_IsRebasedEvenWhenLastSampleIsNotInTheFuture()
    {
        var (context, options, clock, time) = Fixture();
        context.VarDiff.LastRetargetTimestamp = time.GetTimestamp() + 10 * TimeSpan.TicksPerSecond;
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(time.GetTimestamp(), context.VarDiff.LastRetargetTimestamp);
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
        var (context, options, clock, time) = Fixture();
        if(double.IsFinite(difficulty))
            context.SetDifficulty(difficulty);
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => context.SetDifficulty(difficulty));
            // Simulate corrupted legacy state past the new assignment guard so
            // the manager's independent arithmetic defense remains covered.
            typeof(WorkerContextBase).GetField("difficulty",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context, difficulty);
        }
        options.TargetTime = target;
        Assert.Null(VarDiffManager.Update(context, options, clock, maximum));
        Assert.Null(context.VarDiff.LastUpdate);
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock, maximum));
        Assert.Null(context.VarDiff.LastUpdate);
    }

    [Theory]
    [InlineData("inverted")]
    [InlineData("infinite")]
    [InlineData("nan")]
    [InlineData("zero")]
    public void InvalidBounds_StillRecordRealSharesAndRecoverWithoutAFrozenInterval(string invalid)
    {
        var time = new ManualTimeProvider();
        var clock = new MockMasterClock();
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        var worker = new WorkerContextBase();
        worker.Init(10, options, clock, time);
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        var baseline = worker.VarDiff.LastRetargetTimestamp;
        options.MaxDiff = invalid switch { "infinite" => double.PositiveInfinity, "nan" => double.NaN,
            "zero" => 0, _ => 0.5 };
        time.AdvanceMonotonic(TimeSpan.FromSeconds(3));
        Assert.Null(VarDiffManager.Update(worker, options, clock,
            protocolMaximum: invalid == "infinite" ? double.PositiveInfinity : double.MaxValue));
        Assert.Equal(time.GetTimestamp(), worker.VarDiff.LastShareTimestamp);
        Assert.Equal(new[] { 3d }, worker.VarDiff.TimeBuffer.ToArray());
        Assert.Equal(baseline, worker.VarDiff.LastRetargetTimestamp);
        Assert.Null(worker.VarDiff.LastUpdate);
        Assert.Null(VarDiffManager.IdleUpdate(worker, options, clock));
        Assert.Equal(new[] { 3d }, worker.VarDiff.TimeBuffer.ToArray());
        options.MaxDiff = null;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        Assert.Equal(40, VarDiffManager.Update(worker, options, clock));
        Assert.Empty(worker.VarDiff.TimeBuffer); // A changed assignment starts a new window.
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.000001)]
    public void ZeroAverage_DoesNotLowerDifficultyForSubsecondTargets(double target)
    {
        var (context, options, clock, time) = Fixture();
        options.TargetTime = target;
        Assert.Null(VarDiffManager.Update(context, options, clock));
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(10, context.Difficulty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledVarDiff_IsANoOp(bool idle)
    {
        var (context, options, clock, time) = Fixture();
        context.VarDiff = null;
        Assert.Null(idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock));
        Assert.Equal(10, context.Difficulty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NoOpIdleSweep_PreservesRealShareIntervalAndSubsequentRetarget(bool atMinimum, bool limitDelta)
    {
        var (context, options, clock, time) = Fixture();
        options.MinDiff = atMinimum ? 10 : 1;
        options.MaxDelta = limitDelta ? 2 : null;
        context.VarDiff.TimeBuffer = null;
        var previousTs = context.VarDiff.LastShareTimestamp;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(atMinimum ? 30 : 10));
        clock.CurrentTime = clock.Now.AddSeconds(atMinimum ? 30 : 10);
        // Repeated scheduler evaluations are not extra share observations.
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Null(VarDiffManager.IdleUpdate(context, options, clock));
        Assert.Equal(previousTs, context.VarDiff.LastShareTimestamp);
        Assert.Equal(900 * TimeSpan.TicksPerSecond, context.VarDiff.LastRetargetTimestamp);
        Assert.Null(context.VarDiff.LastUpdate);
        Assert.Null(context.VarDiff.TimeBuffer);
        Assert.Null(VarDiffManager.Update(context, options, clock));
        Assert.Equal(atMinimum ? 30d : 10d, context.VarDiff.TimeBuffer.Front());
        // Real, faster intervals eventually replace the slow observation.
        double? result = null;
        for(var i = 0; i < 11 && result == null; i++)
        {
            time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
            clock.CurrentTime = clock.Now.AddSeconds(1);
            result = VarDiffManager.Update(context, options, clock);
        }
        Assert.NotNull(result);
        Assert.InRange(result.Value, 10.000001, limitDelta ? 12 : 100);
    }

    [Theory]
    [InlineData(0, false, 100000d)]
    [InlineData(1, false, 200000d)]
    [InlineData(4, false, 500000d)]
    [InlineData(9, false, 1000000d)]
    [InlineData(10, false, 1000000d)]
    [InlineData(0, true, 100000d)]
    [InlineData(1, true, 200000d)]
    [InlineData(4, true, 500000d)]
    [InlineData(9, true, 1000000d)]
    [InlineData(10, true, 1000000d)]
    public void ZeroAverage_EstimateUsesAvailableIntervals(int storedSamples, bool idle, double expected)
    {
        var (context, options, clock, time) = Fixture(idleBurst: idle);
        context.VarDiff.TimeBuffer = new CircularBuffer<double>(10);
        for(var i = 0; i < storedSamples; i++)
            context.VarDiff.TimeBuffer.PushBack(0);
        Assert.Equal(expected, idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseZeroWindow_DoesNotLowerDifficultyBelowItsEstimate(bool idle)
    {
        var (context, options, clock, time) = Fixture();
        context.VarDiff.TimeBuffer = null;
        options.TargetTime = 0.0005;
        Assert.Null(idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubmillisecondSamples_AreMeasuredInsteadOfUnixQuantized(bool idle)
    {
        var (context, options, clock, time) = Fixture();
        context.VarDiff.TimeBuffer = null;
        context.VarDiff.LastRetargetTimestamp = time.GetTimestamp();
        for(var i = 0; i < 9; i++)
        {
            time.AdvanceMonotonic(TimeSpan.FromTicks(1000)); // 0.1 ms, wall clock frozen.
            Assert.Null(VarDiffManager.Update(context, options, clock));
        }
        Assert.Equal(9, context.VarDiff.TimeBuffer.Size);
        Assert.All(context.VarDiff.TimeBuffer, x => Assert.Equal(0.0001, x));
        options.RetargetTime = 1e-7;
        if(idle)
            time.AdvanceMonotonic(TimeSpan.FromTicks(1));
        context.VarDiff.LastRetargetTimestamp -= 100 * TimeSpan.TicksPerSecond;
        var difficulty = idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock);
        Assert.Equal(1000000, difficulty.Value); // Measured intervals remain, inferred rate is floored.
        context.SetDifficulty(difficulty.Value);
        time.AdvanceMonotonic(TimeSpan.FromMilliseconds(5));
        context.VarDiff.LastRetargetTimestamp -= 100 * TimeSpan.TicksPerSecond;
        var measured = idle ? VarDiffManager.IdleUpdate(context, options, clock) :
            VarDiffManager.Update(context, options, clock);
        Assert.InRange(measured.Value / (difficulty.Value * 10 / 0.005), 0.999999999, 1.000000001);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(0d)]
    public void TimingConfiguration_RejectsNonfiniteAndNonpositiveValues(double value)
    {
        var (_, options, _, _) = Fixture();
        var validator = new VarDiffConfigValidator();
        options.TargetTime = value;
        Assert.Contains(validator.Validate(options).Errors, x => x.PropertyName == nameof(options.TargetTime));
        options.TargetTime = 10;
        options.RetargetTime = value;
        Assert.Contains(validator.Validate(options).Errors, x => x.PropertyName == nameof(options.RetargetTime));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0d, true)]
    [InlineData(2d, true)]
    [InlineData(double.MaxValue, true)]
    [InlineData(-1d, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void DeltaConfiguration_PreservesDisabledLimitAndRejectsInvalidValues(double? value, bool valid)
    {
        var (_, options, _, _) = Fixture();
        options.MaxDelta = value;
        Assert.Equal(valid, new VarDiffConfigValidator().Validate(options).IsValid);
    }
}
