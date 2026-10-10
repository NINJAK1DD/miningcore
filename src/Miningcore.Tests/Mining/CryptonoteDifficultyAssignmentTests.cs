using System;
using System.Collections.Generic;
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
                Math.BitIncrement(CryptonoteDifficulty.Maximum), double.MaxValue })
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
        worker.SetDifficulty(CryptonoteDifficulty.Maximum);
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
        foreach(var value in new[] { 0.5d, 0.1d, 1d / 256d, double.MaxValue })
        {
            var options = new VarDiffConfig { MinDiff = 1, MaxDiff = 100 };
            var endpoint = new PoolEndpoint { Difficulty = 10, VarDiff = options };
            if(field == "endpoint") endpoint.Difficulty = value;
            if(field == "minimum") options.MinDiff = value;
            if(field == "maximum") options.MaxDiff = value;
            Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.ValidatePool(new PoolConfig
                { Ports = new Dictionary<int, PoolEndpoint> { [3333] = endpoint } }));
        }
    }

    [Fact]
    public void UpperBoundary_HasPositiveNonOverflowingQuantizedDivisor()
    {
        Assert.True(checked((long) (CryptonoteDifficulty.Validate(CryptonoteDifficulty.Maximum) * 255d)) > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.Validate(Math.BitIncrement(CryptonoteDifficulty.Maximum)));
    }
}
