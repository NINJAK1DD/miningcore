using System;
using System.Collections.Generic;
using System.Linq;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Miningcore.VarDiff;
using Xunit;

namespace Miningcore.Tests.Mining;

public class CryptonoteDifficultyAssignmentTests
{
    public static IEnumerable<object[]> InvalidAssignments
    {
        get
        {
            foreach(var type in new[] { typeof(ConcealWorkerContext), typeof(CryptonoteWorkerContext), typeof(ZanoWorkerContext) })
            foreach(var value in new[] { 0.5d, 0.1d, 1d / 256d, double.Epsilon,
                Math.BitIncrement(CryptonoteDifficulty.FullTargetMaximum), double.MaxValue })
                yield return new object[] { type, value };
            foreach(var type in new[] { typeof(ConcealWorkerContext), typeof(CryptonoteWorkerContext) })
            foreach(var value in new[] { Math.BitIncrement(CryptonoteDifficulty.ShortTargetMaximum),
                1e8d, 1e9d, 2.2e9d, 3e9d, Math.BitDecrement(4294967296d),
                4294967296d, Math.BitIncrement(4294967296d), 5e9d, CryptonoteDifficulty.FullTargetMaximum })
                yield return new object[] { type, value };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidAssignments))]
    public void EveryAssignmentBoundary_RejectsBeforeMutatingPendingOrPreviousCredit(Type type, double value)
    {
        var worker = (WorkerContextBase) Activator.CreateInstance(type);
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 1 };
        worker.Init(1, options, new MockMasterClock());
        worker.SetDifficulty(2);
        worker.EnqueueNewDifficulty(3);
        var original = worker.VarDiff;
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.Init(value, options, new MockMasterClock()));
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.SetDifficulty(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.Difficulty = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.PreviousDifficulty = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.EnqueueNewDifficulty(value));
        Assert.Equal(2, worker.Difficulty);
        Assert.Equal(1, worker.PreviousDifficulty);
        Assert.Same(original, worker.VarDiff);
        Assert.True(worker.ApplyPendingDifficulty());
        Assert.Equal(3, worker.Difficulty);
        Assert.Equal(2, worker.PreviousDifficulty);
    }

    [Theory]
    [InlineData(typeof(ConcealWorkerContext))]
    [InlineData(typeof(CryptonoteWorkerContext))]
    [InlineData(typeof(ZanoWorkerContext))]
    public void DynamicCalculation_RespectsProtocolFloorCeilingAndNoOpMetadata(Type type)
    {
        var worker = (WorkerContextBase) Activator.CreateInstance(type);
        var time = new ManualTimeProvider();
        var clock = new MockMasterClock();
        var options = new VarDiffConfig { MinDiff = 0.1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        worker.Init(1, options, clock, time);
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(100));
        Assert.Null(VarDiffManager.IdleUpdate(worker, options, clock));
        Assert.Null(worker.VarDiff.LastUpdate);
        worker.SetDifficulty(10);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(100));
        Assert.Equal(1, VarDiffManager.IdleUpdate(worker, options, clock));
        worker.SetDifficulty(worker.MaximumDifficulty);
        worker.VarDiff = new VarDiffContext(time) { Config = options };
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        Assert.Null(worker.VarDiff.LastUpdate);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("minimum")]
    [InlineData("maximum")]
    public void Configuration_RejectsUnrepresentableBounds(string field)
    {
        foreach(var (validate, maximum) in new (Action<PoolConfig>, double)[]
        {
            (CryptonoteDifficulty.ValidateShortTargetPool, CryptonoteDifficulty.ShortTargetMaximum),
            (CryptonoteDifficulty.ValidateFullTargetPool, CryptonoteDifficulty.FullTargetMaximum),
        })
        foreach(var value in new[] { 0.5d, 0.1d, 1d / 256d, Math.BitIncrement(maximum), double.MaxValue })
        {
            var options = new VarDiffConfig { MinDiff = 1, MaxDiff = 100 };
            var endpoint = new PoolEndpoint { Difficulty = 10, VarDiff = options };
            if(field == "endpoint") endpoint.Difficulty = value;
            if(field == "minimum") options.MinDiff = value;
            if(field == "maximum") options.MaxDiff = value;
            Assert.Throws<ArgumentOutOfRangeException>(() => validate(new PoolConfig
                { Ports = new Dictionary<int, PoolEndpoint> { [3333] = endpoint } }));
        }
    }

    [Fact]
    public void UpperBoundary_HasPositiveNonOverflowingQuantizedDivisor()
    {
        Assert.True(checked((long) (CryptonoteDifficulty.Validate(CryptonoteDifficulty.FullTargetMaximum, CryptonoteDifficulty.FullTargetMaximum) * 255d)) > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.Validate(Math.BitIncrement(CryptonoteDifficulty.FullTargetMaximum), CryptonoteDifficulty.FullTargetMaximum));
    }

    [Theory]
    [InlineData(typeof(ConcealWorkerContext))]
    [InlineData(typeof(CryptonoteWorkerContext))]
    public void FractionalAssignments_NormalizeEveryStoredDifficultyAndKeepHintsWithinPolicy(Type type)
    {
        var worker = (WorkerContextBase) Activator.CreateInstance(type);
        foreach(var requested in new[] { 1.5, 1.99, 2.5, 55.75, 100.5 })
        {
            var expected = Math.Floor(requested);
            worker.Init(requested, null, new MockMasterClock());
            Assert.Equal(expected, worker.Difficulty);
            worker.Difficulty = requested;
            worker.PreviousDifficulty = requested;
            worker.EnqueueNewDifficulty(requested);
            Assert.True(worker.ApplyPendingDifficulty());
            Assert.Equal(expected, worker.Difficulty);
            Assert.Equal(expected, worker.PreviousDifficulty);
            worker.SetDifficulty(requested);
            Assert.Equal(expected, worker.Difficulty);
        }
        worker.Init(2, new VarDiffConfig { MinDiff = 2.5 }, new MockMasterClock());
        var original = worker.VarDiff;
        Assert.False(CryptonoteDifficulty.TryApplyStaticHint(worker, 2.99, null));
        Assert.Equal(2, worker.Difficulty);
        Assert.Same(original, worker.VarDiff);
        Assert.True(CryptonoteDifficulty.TryApplyStaticHint(worker, 3.5, null));
        Assert.Equal(3, worker.Difficulty);
        Assert.Null(worker.VarDiff);
    }

    [Fact]
    public void Zano_FractionalAssignmentsRemainUnchanged()
    {
        var worker = new ZanoWorkerContext();
        worker.Init(1.99, null, new MockMasterClock());
        worker.EnqueueNewDifficulty(2.5);
        Assert.True(worker.ApplyPendingDifficulty());
        Assert.Equal(1.99, worker.PreviousDifficulty);
        Assert.Equal(2.5, worker.Difficulty);
    }

    [Theory]
    [InlineData(typeof(ConcealWorkerContext), false)]
    [InlineData(typeof(ConcealWorkerContext), true)]
    [InlineData(typeof(CryptonoteWorkerContext), false)]
    [InlineData(typeof(CryptonoteWorkerContext), true)]
    public void QuantizedVarDiff_PreservesNoOpSamplesBoundsAndSubUnitDelta(Type type, bool idle)
    {
        var worker = (WorkerContextBase) Activator.CreateInstance(type);
        var time = new ManualTimeProvider();
        var clock = new MockMasterClock();
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        worker.Init(2.5, options, clock, time);
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        var baseline = worker.VarDiff.LastRetargetTimestamp;
        var share = worker.VarDiff.LastShareTimestamp;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(8));
        double? Evaluate() => idle ? VarDiffManager.IdleUpdate(worker, options, clock) : VarDiffManager.Update(worker, options, clock);
        Assert.Null(Evaluate()); // 2.5 rounds back to the current 2.
        Assert.Equal(baseline, worker.VarDiff.LastRetargetTimestamp);
        Assert.Null(worker.VarDiff.LastUpdate);
        Assert.Equal(idle ? share : time.GetTimestamp(), worker.VarDiff.LastShareTimestamp);
        if(!idle) Assert.Equal(new[] { 8d }, worker.VarDiff.TimeBuffer.ToArray());
        worker.SetDifficulty(3);
        worker.VarDiff = new VarDiffContext(time) { Config = options };
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        options.MaxDelta = 0.5;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(100));
        Assert.Null(Evaluate()); // A floor to 2 would violate MaxDelta.
        Assert.Null(worker.VarDiff.LastUpdate);
        options.MaxDelta = null;
        options.MinDiff = 2.5;
        options.MaxDiff = 3.5;
        Assert.Null(Evaluate()); // The only whole-number value in the range is 3.
        Assert.Null(worker.VarDiff.LastUpdate);
        options.MinDiff = 3.1;
        options.MaxDiff = 3.9;
        Assert.Null(Evaluate()); // No integer; real-share bookkeeping still advances.
        options.MinDiff = 1.5;
        options.MaxDiff = 7.5;
        worker.VarDiff = new VarDiffContext(time) { Config = options };
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(4));
        var calculated = Evaluate(); // Candidate 7.5, effective max 7.
        Assert.Equal(7, calculated);
        Assert.Equal(clock.Now, worker.VarDiff.LastUpdate);
        worker.EnqueueNewDifficulty(calculated.Value);
        Assert.True(worker.ApplyPendingDifficulty());
        Assert.Equal(7, worker.Difficulty);
    }

    [Fact]
    public void Configuration_RejectsRangesWithoutAnyWholeNumberAssignment()
    {
        var config = new PoolConfig { Ports = new Dictionary<int, PoolEndpoint>
            { [3333] = new() { Difficulty = 3, VarDiff = new() { MinDiff = 2.1, MaxDiff = 2.9 } } } };
        Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.ValidateShortTargetPool(config));
        CryptonoteDifficulty.ValidateFullTargetPool(config);
    }

    [Fact]
    public void Configuration_RejectsFrozenDeltaAndIncompatibleNormalizedStart()
    {
        // Exact review configuration: floor(1.5)=1, but effective range is [2,2].
        var options = new VarDiffConfig { MinDiff = 1.5, MaxDiff = 2.5, MaxDelta = 0.5,
            TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        var endpoint = new PoolEndpoint { Difficulty = 1.5, VarDiff = options };
        var config = new PoolConfig { Ports = new Dictionary<int, PoolEndpoint> { [3333] = endpoint } };
        Assert.True(new PoolEndpointValidator().Validate(endpoint).IsValid);
        var frozen = Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.ValidateShortTargetPool(config));
        Assert.Contains("maxDelta", frozen.Message);
        options.MaxDelta = 1;
        var start = Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.ValidateShortTargetPool(config));
        Assert.Contains("Normalized endpoint difficulty", start.Message);
        endpoint.Difficulty = 3.5;
        Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.ValidateShortTargetPool(config));
        endpoint.Difficulty = 2.5;
        foreach(var invalid in new[] { double.Epsilon, 0.5, Math.BitDecrement(1d), -1, double.NaN, double.PositiveInfinity })
        {
            options.MaxDelta = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.ValidateShortTargetPool(config));
        }
        foreach(var valid in new double?[] { null, 0, 1, 1.5, double.MaxValue })
        {
            options.MaxDelta = valid;
            CryptonoteDifficulty.ValidateShortTargetPool(config);
        }
        endpoint.Difficulty = 1.5;
        options.MaxDelta = 0.5;
        CryptonoteDifficulty.ValidateFullTargetPool(config); // Zano retains continuous policy.
        Assert.Equal(1.5, endpoint.Difficulty); // Validation never reconciles config silently.
    }

    [Theory]
    [InlineData(typeof(ConcealWorkerContext), false)]
    [InlineData(typeof(ConcealWorkerContext), true)]
    [InlineData(typeof(CryptonoteWorkerContext), false)]
    [InlineData(typeof(CryptonoteWorkerContext), true)]
    public void QuantizedVarDiff_ConflictingStartupRangeCannotOverrideMaxDelta(Type type, bool idle)
    {
        var worker = (WorkerContextBase) Activator.CreateInstance(type);
        var time = new ManualTimeProvider();
        var clock = new MockMasterClock();
        var options = new VarDiffConfig { MinDiff = 1.5, MaxDiff = 2.5, MaxDelta = 0.5,
            TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        // Simulate legacy/runtime state independently of the now-strict startup check.
        worker.Init(1.5, options, clock, time);
        worker.EnqueueNewDifficulty(3);
        Assert.Equal(1, worker.Difficulty);
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        var baseline = worker.VarDiff.LastRetargetTimestamp;
        var previousShare = worker.VarDiff.LastShareTimestamp;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        double? Evaluate() => idle ? VarDiffManager.IdleUpdate(worker, options, clock) : VarDiffManager.Update(worker, options, clock);
        Assert.Null(Evaluate()); // [2,2] intersect [0.5,1.5] has no integer.
        Assert.Equal(1, worker.Difficulty);
        Assert.True(worker.HasPendingDifficulty);
        Assert.Equal(baseline, worker.VarDiff.LastRetargetTimestamp);
        Assert.Null(worker.VarDiff.LastUpdate);
        Assert.Equal(idle ? previousShare : time.GetTimestamp(), worker.VarDiff.LastShareTimestamp);
        if(!idle) Assert.Equal(new[] { 5d }, worker.VarDiff.TimeBuffer.ToArray());
        options.MaxDelta = 1;
        var calculated = Evaluate();
        Assert.Equal(2, calculated);
        Assert.InRange(Math.Abs(calculated.Value - worker.Difficulty), 0, options.MaxDelta.Value);
        worker.EnqueueNewDifficulty(calculated.Value);
        Assert.True(worker.ApplyPendingDifficulty());
        Assert.Equal(2, worker.Difficulty);
        // Also protect an above-range worker from an excessive downward clamp.
        worker.SetDifficulty(5);
        worker.VarDiff = new VarDiffContext(time) { Config = options };
        Assert.Null(VarDiffManager.Update(worker, options, clock));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(100));
        Assert.Null(Evaluate()); // [2,2] intersect [4,6] is empty.
        Assert.Null(worker.VarDiff.LastUpdate);
        options.MaxDelta = 3;
        Assert.Equal(2, Evaluate()); // Feasible intersection now contains 2.
    }

    [Theory]
    [InlineData(typeof(ConcealWorkerContext), false)]
    [InlineData(typeof(ConcealWorkerContext), true)]
    [InlineData(typeof(CryptonoteWorkerContext), false)]
    [InlineData(typeof(CryptonoteWorkerContext), true)]
    public void QuantizedVarDiff_FinalAssignmentsSatisfyRangeAndDeltaTogether(Type type, bool idle)
    {
        foreach(var starting in new[] { 1d, 2d, 4d, 7d, 10d })
        foreach(var min in new[] { 1d, 1.5, 3.1 })
        foreach(var max in new[] { 3.5, 6d, 10.5 })
        foreach(var delta in new[] { 0d, 0.5, 1d, 1.5, 3d })
        foreach(var seconds in new[] { 1d, 5d, 30d })
        {
            var worker = (WorkerContextBase) Activator.CreateInstance(type);
            var time = new ManualTimeProvider();
            var clock = new MockMasterClock();
            var options = new VarDiffConfig { MinDiff = min, MaxDiff = max, MaxDelta = delta,
                TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
            worker.Init(starting, options, clock, time);
            Assert.Null(VarDiffManager.Update(worker, options, clock));
            var baseline = worker.VarDiff.LastRetargetTimestamp;
            time.AdvanceMonotonic(TimeSpan.FromSeconds(seconds));
            var calculated = idle ? VarDiffManager.IdleUpdate(worker, options, clock) : VarDiffManager.Update(worker, options, clock);
            if(calculated.HasValue)
            {
                Assert.Equal(Math.Floor(calculated.Value), calculated.Value);
                Assert.InRange(calculated.Value, Math.Ceiling(min), Math.Floor(max));
                if(delta > 0) Assert.InRange(Math.Abs(calculated.Value - starting), 0, delta);
            }
            else
            {
                Assert.Equal(baseline, worker.VarDiff.LastRetargetTimestamp);
                Assert.Null(worker.VarDiff.LastUpdate);
            }
        }
    }
}
