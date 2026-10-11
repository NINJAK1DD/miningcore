using System;
using System.Collections.Generic;
using System.Linq;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.VarDiff;
using Xunit;

namespace Miningcore.Tests.Mining;

public class IssuedDifficultyTests
{
    public static IEnumerable<object[]> Families => typeof(WorkerContextBase).Assembly.GetTypes()
        .Where(x => typeof(WorkerContextBase).IsAssignableFrom(x) && !x.IsAbstract)
        .SelectMany(x => new[] { "static", "nicehash", "minimum" }
            .Select(mode => new object[] { x.FullName, mode }));

    [Theory]
    [MemberData(nameof(Families))]
    public void AllFamilies_FixedAssignmentPreservesOriginalCreditAndRejectsNewWork(string family, string mode)
    {
        var worker = (WorkerContextBase) Activator.CreateInstance(typeof(WorkerContextBase).Assembly.GetType(family));
        var time = new ManualTimeProvider();
        var wall = new MockMasterClock();
        var config = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 5, VariancePercent = 1 };
        worker.Init(10, config, wall, time);
        worker.SetDifficulty(20); // Dynamic assignment already occurred.
        worker.VarDiff.LastUpdate = wall.Now;
        var template = new object();
        var old = Issue(worker, template);
        if(mode == "nicehash") worker.UserAgent = "NiceHash";
        worker.VarDiff = null;
        worker.SetDifficulty(40);
        var current = Issue(worker, template);
        Assert.NotSame(old, current);
        Assert.Equal(20, worker.ValidateShareDifficulty(25, false, old));
        Assert.Equal(20, worker.ValidateShareDifficulty(100, false, old));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(25, false, current));
        Assert.Equal(40, worker.ValidateShareDifficulty(40, false, current));
        time.MoveWallClock(TimeSpan.FromDays(-1000));
        wall.CurrentTime = DateTime.MaxValue;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.Equal(20, worker.ValidateShareDifficulty(25, false, old));
        worker.SetDifficulty(40); // No-op must not extend grace.
        worker.VarDiff = new VarDiffContext(new ManualTimeProvider()) { Config = config };
        Assert.Same(current, Issue(worker, template));
        time.AdvanceMonotonic(TimeSpan.FromTicks(1));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(25, false, old));
        Assert.Equal(20, worker.ValidateShareDifficulty(25, true, old)); // Candidate exception keeps original credit.
    }

    private static object Issue(WorkerContextBase worker, object template) =>
        worker.JobForDifficulty(template, (_, _) => new object(), "job");

    [Fact]
    public void RepeatedChangesAndReturningToOldValueCannotRenewRetiredWork()
    {
        var worker = new WorkerContextBase();
        var time = new ManualTimeProvider();
        worker.Init(10, null, new MockMasterClock(), time);
        var template = new object();
        var a = Issue(worker, template);
        worker.SetDifficulty(20);
        var b = Issue(worker, template);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(20));
        worker.SetDifficulty(10);
        var c = Issue(worker, template);
        Assert.NotSame(a, c);
        Assert.Equal(10, worker.ValidateShareDifficulty(10, false, c));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(10, false, b));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(100, false, a));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(100, false, b));
        Assert.Equal(10, worker.ValidateShareDifficulty(10, false, c));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MalformedPreviousStateCannotAuthorizeOrInflateProofs(double invalid)
    {
        var worker = new WorkerContextBase();
        worker.Init(40, null, new MockMasterClock());
        worker.PreviousDifficulty = invalid;
        worker.VarDiff = new VarDiffContext { LastUpdate = DateTime.MaxValue };
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, new object()));
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.SetDifficulty(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.EnqueueNewDifficulty(invalid));
        Assert.Equal(40, worker.Difficulty);
    }

    [Fact]
    public void BlobAssignmentsKeepOriginalCreditAndAreRemovedOnEviction()
    {
        var worker = new WorkerContextBase();
        var time = new ManualTimeProvider();
        worker.Init(10, null, new MockMasterClock(), time);
        var template = new object();
        worker.RegisterBlobDifficulty(template, 1);
        worker.SetDifficulty(20);
        worker.RegisterBlobDifficulty(template, 2);
        Assert.Equal(10, worker.ValidateShareDifficulty(15, false, (template, 1u)));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(15, false, (template, 2u)));
        worker.ForgetBlobDifficulty(template, 1);
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(15, false, (template, 1u)));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.Equal(20, worker.ValidateShareDifficulty(20, false, (template, 2u)));
    }

    [Fact]
    public void BlobExtraNonceReuseInNewTemplatesCannotReuseAnEasierAssignment()
    {
        var worker = new WorkerContextBase();
        worker.Init(10, null, new MockMasterClock());
        var oldTemplate = new object();
        var newTemplate = new object();
        worker.RegisterBlobDifficulty(oldTemplate, 1);
        worker.SetDifficulty(40);
        worker.RegisterBlobDifficulty(newTemplate, 1);
        Assert.Equal(10, worker.ValidateShareDifficulty(20, false, (oldTemplate, 1u)));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, (newTemplate, 1u)));
        worker.ForgetBlobDifficulty(oldTemplate, 1);
        Assert.Equal(40, worker.ValidateShareDifficulty(40, false, (newTemplate, 1u)));
    }

    [Fact]
    public void UnissuedPreviousDifficultyAndBackwardCounterCannotAuthorizeAProof()
    {
        var worker = new WorkerContextBase();
        var time = new ManualTimeProvider();
        worker.Init(10, null, new MockMasterClock(), time);
        var old = Issue(worker, new object());
        worker.SetDifficulty(40);
        worker.PreviousDifficulty = 1;
        worker.VarDiff = new VarDiffContext { LastUpdate = DateTime.MaxValue };
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, new object()));
        time.AdvanceMonotonic(TimeSpan.FromTicks(-1));
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(20, false, old));
    }
}
