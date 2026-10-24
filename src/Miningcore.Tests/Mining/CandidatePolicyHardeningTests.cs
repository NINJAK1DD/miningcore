using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Xunit;

namespace Miningcore.Tests.Mining;

public class CandidatePolicyHardeningTests
{
    private sealed class Policy : ICandidateCreditJob
    {
        internal int Reads;
        internal double Target = 10;
        public double CandidateDifficulty { get { System.Threading.Interlocked.Increment(ref Reads); return Target; } }
    }

    [Fact]
    public void IssuedPolicyIsCachedAndSharedWithoutReevaluatingMutableTemplate()
    {
        var source = new Policy();
        CandidateShareCredit.Initialize(source);
        var worker = new WorkerContextBase();
        worker.Init(1000, null, new MockMasterClock());
        var first = worker.JobForDifficulty(source, (_, _) => new Policy(), "1");
        var reads = source.Reads;
        source.Target = double.NaN;
        worker.SetDifficulty(2000);
        var second = worker.JobForDifficulty(source, (_, _) => new Policy(), "2");
        Assert.Equal(10, worker.ValidateShareDifficulty(1e6, true, first));
        Assert.Equal(10, worker.ValidateShareDifficulty(1e6, true, second));
        for(var i = 0; i < 100; i++) Assert.Equal(10, CandidateShareCredit.Limit(source));
        Assert.Equal(reads, source.Reads);
        Assert.Equal(0, second.Reads);
    }

    [Fact]
    public void InvalidTemplateIsRejectedBeforeIssuanceAndPreviousCandidatesRemainCreditable()
    {
        var worker = new WorkerContextBase();
        worker.Init(1000, null, new MockMasterClock());
        var valid = worker.JobForDifficulty(new Policy(), (_, _) => throw new Exception(), "valid");
        foreach(var target in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            var invalid = new Policy { Target = target };
            Assert.Throws<InvalidDataException>(() => CandidateShareCredit.Initialize(invalid));
            Assert.Throws<InvalidDataException>(() => worker.JobForDifficulty(invalid, (_, _) => throw new Exception(), "invalid"));
            Assert.Equal(10, worker.ValidateShareDifficulty(20, true, valid));
        }
        Assert.Throws<InvalidDataException>(() => CandidateShareCredit.Limit(new object()));
        Assert.Equal(double.MaxValue, CandidateShareCredit.Limit(new BitcoinJob()));
    }

    [Fact]
    public void UnknownPoliciesFailBeforeAnySnapshotOrBlobIssuance()
    {
        var worker = new WorkerContextBase();
        worker.Init(100, null, new MockMasterClock());
        var copied = false;
        Assert.Throws<InvalidDataException>(() => worker.JobForDifficulty(new object(), (_, _) => { copied = true; return new object(); }, "unknown"));
        Assert.False(copied);
        Assert.Throws<InvalidDataException>(() => worker.RegisterBlobDifficulty(new object(), 1));
        var bitcoin = worker.JobForDifficulty(new BitcoinJob(), (_, _) => throw new Exception(), "bitcoin");
        Assert.Equal(100, worker.ValidateShareDifficulty(200, true, bitcoin));
    }

    [Fact]
    public void ConcurrentInitializationAndReadsReplacePolicyAtomically()
    {
        var job = new Policy();
        CandidateShareCredit.Initialize(job);
        System.Threading.Tasks.Parallel.For(0, 1000, i =>
        {
            if(i % 2 == 0) CandidateShareCredit.Initialize(job);
            Assert.Equal(10, CandidateShareCredit.Limit(job));
        });
    }

    [Fact]
    public void HardCeilingOverridesMaxDeltaThenUsesLeastExcessFloorAndResumes()
    {
        var time = new ManualTimeProvider();
        var clock = new MockMasterClock();
        var worker = new WorkerContextBase();
        var options = new VarDiffConfig { MinDiff = 1, MaxDiff = 10000, MaxDelta = 1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        worker.Init(512, options, clock, time);
        Assert.True(CandidateShareCredit.BoundAutomaticAssignment(worker, 20));
        worker.ApplyPendingDifficulty();
        Assert.Equal(20, worker.Difficulty); // Greater than MaxDelta=1 reduction is intentional.
        worker.SetDifficulty(512);
        options.MinDiff = 30;
        worker.EnqueueNewDifficulty(900);
        Assert.False(CandidateShareCredit.BoundAutomaticAssignment(worker, 5));
        worker.ApplyPendingDifficulty();
        Assert.Equal(30, worker.Difficulty);
        Assert.False(worker.HasPendingDifficulty);
        Assert.True(CandidateShareCredit.BoundAutomaticAssignment(worker, 100));
        Assert.Null(Miningcore.VarDiff.VarDiffManager.Update(worker, options, clock, 100));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        var next = Miningcore.VarDiff.VarDiffManager.Update(worker, options, clock, 100);
        Assert.NotNull(next);
        Assert.InRange(next.Value, 30, 100);
        worker.EnqueueNewDifficulty(next.Value);
        worker.ApplyPendingDifficulty();
        Assert.True(worker.Difficulty > 30);
    }

    [Fact]
    public void ExplicitFloorCannotMaskInvalidAutomaticAssignments()
    {
        var worker = new WorkerContextBase();
        worker.Init(10, null, new MockMasterClock());
        worker.SetDifficulty(100, DifficultyOrigin.Device);
        foreach(var invalid in new[] { 0d, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => worker.SetDifficulty(invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => worker.EnqueueNewDifficulty(invalid));
            Assert.Equal(100, worker.Difficulty);
            Assert.False(worker.HasPendingDifficulty);
        }
    }

    // Inspect actual compiled calls, including async state machines and derived
    // custom verifiers. A newly introduced consumer must explicitly choose policy.
    [Fact]
    public void EveryProductionDifficultyValidatorHasReviewedPolicy()
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode) f.GetValue(null))
            .ToDictionary(x => unchecked((ushort) x.Value));
        var callers = new HashSet<Type>();
        foreach(var type in typeof(BitcoinJob).Assembly.GetTypes())
        foreach(var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if(il == null) continue;
            for(var i = 0; i < il.Length;)
            {
                ushort key = il[i++];
                if(key == 0xfe) key = (ushort) (0xfe00 | il[i++]);
                var op = codes[key];
                if(op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase called;
                    try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), type.GetGenericArguments(), method.GetGenericArguments()); }
                    catch(ArgumentException) { called = null; }
                    if(called?.DeclaringType == typeof(WorkerContextBase) && called.Name is "ValidateShareDifficulty" or "TryValidateShareDifficulty")
                    {
                        var owner = type;
                        while(owner.IsNested) owner = owner.DeclaringType;
                        if(owner.Namespace?.StartsWith("Miningcore.Blockchain.", StringComparison.Ordinal) == true)
                            callers.Add(owner);
                    }
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, i) * 4,
                    _ => 4
                };
            }
        }
        Assert.True(callers.Count >= 27);
        foreach(var caller in callers)
            Assert.True(typeof(ICandidateCreditJob).IsAssignableFrom(caller) || caller == typeof(BitcoinJob) ||
                caller == typeof(Miningcore.Blockchain.Bitcoin.MergedMining.MergedMiningBitcoinJob), caller.FullName);
    }
}
