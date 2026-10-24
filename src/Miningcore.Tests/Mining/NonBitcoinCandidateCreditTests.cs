using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Warthog;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using Xunit;

namespace Miningcore.Tests.Mining;

public class NonBitcoinCandidateCreditTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    public static IEnumerable<object[]> Jobs => typeof(BitcoinJob).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(ICandidateCreditJob).IsAssignableFrom(type))
        .SelectMany(type => new[] { new object[] { type.FullName, false }, new object[] { type.FullName, true } });

    internal static void Set(object target, string name, object value)
    {
        for(var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, Members);
            if(property?.SetMethod != null) { property.SetValue(target, value); return; }
            var field = type.GetField(name, Members) ?? type.GetField($"<{name}>k__BackingField", Members);
            if(field != null) { field.SetValue(target, value); return; }
        }
        throw new InvalidOperationException($"Missing fixture member {target.GetType()}.{name}");
    }

    // Initialize accounting metadata only. No synthetic result is presented as a
    // consensus proof; these cases exercise every concrete production policy
    // through the actual immutable issuance/admission/accounting boundary.
    internal static void ConfigurePolicy(object job, double ceiling = 1000)
    {
        if(job is not ICandidateCreditJob) return;
        if(job is Miningcore.Blockchain.Cryptonote.CryptonoteJob or Miningcore.Blockchain.Conceal.ConcealJob or Miningcore.Blockchain.Zano.ZanoJob)
        {
            var property = job.GetType().GetProperty("BlockTemplate");
            var template = property.GetValue(job) ?? Activator.CreateInstance(property.PropertyType);
            var difficulty = property.PropertyType.GetProperty("Difficulty");
            difficulty.SetValue(template, Convert.ChangeType(ceiling, difficulty.PropertyType));
            Set(job, "BlockTemplate", template);
            CandidateShareCredit.Initialize(job);
            return;
        }
        if(job is EthereumJob)
        {
            Set(job, "blockTarget", new Target(EthereumConstants.BigMaxValue / new BigInteger(ceiling * EthereumConstants.Pow2x32)).ToUInt256());
            CandidateShareCredit.Initialize(job);
            return;
        }
        if(job is KaspaJob)
        {
            Set(job, "shareMultiplier", 1d);
            Set(job, "blockTargetValue", new Target(KaspaConstants.Diff1b / (BigInteger) ceiling).ToUInt256());
            CandidateShareCredit.Initialize(job);
            return;
        }
        if(job is ProgpowJob)
        {
            Set(job, "shareMultiplier", 1d);
            var numerator = (BigInteger) job.GetType().GetProperty("CandidateDiff1", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(job);
            Set(job, "blockTargetValue", new Target(numerator / (BigInteger) ceiling).ToUInt256());
            Set(job, "Difficulty", ceiling);
            CandidateShareCredit.Initialize(job);
            return;
        }
        if(job is Miningcore.Blockchain.Nexa.NexaJob or Miningcore.Blockchain.Handshake.HandshakeJob or
            Miningcore.Blockchain.Satoshicash.SatoshicashJob or Miningcore.Blockchain.Xelis.XelisJob)
        {
            Set(job, "shareMultiplier", 1d);
            var numerator = job is Miningcore.Blockchain.Xelis.XelisJob ? Miningcore.Blockchain.Xelis.XelisConstants.Diff1Target : BitcoinConstants.Diff1;
            var bytes = (numerator / (BigInteger) ceiling).ToByteArray(true, false);
            Array.Resize(ref bytes, 32);
            Set(job, "blockTargetValue", new uint256(bytes));
            Set(job, "Difficulty", ceiling);
            CandidateShareCredit.Initialize(job);
            return;
        }
        if(job is WarthogJob)
        {
            Set(job, "IsJanusHash", true);
            Set(job, "network", WarthogNetworkType.Testnet);
            Set(job, "blockTargetValue", new WarthogTarget(ceiling));
            Set(job, "Difficulty", ceiling);
            CandidateShareCredit.Initialize(job);
            return;
        }
        Set(job, "Difficulty", job is AlephiumJob ? ceiling * AlephiumConstants.Pow2xDiff1TargetNumZero : ceiling);
        for(var type = job.GetType(); type != null; type = type.BaseType)
            if(type.GetField("shareMultiplier", Members) is { } field) { field.SetValue(job, 1d); break; }
        CandidateShareCredit.Initialize(job);
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public void EveryConcretePolicy_CapsBothWeakAndStrongCandidatesAtIssuedTarget(string name, bool exceptionallyStrong)
    {
        var type = typeof(BitcoinJob).Assembly.GetType(name);
        var source = RuntimeHelpers.GetUninitializedObject(type);
        ConfigurePolicy(source);
        var limit = CandidateShareCredit.Limit(source);
        Assert.InRange(limit, 999, 1001);
        var time = new ManualTimeProvider();
        var worker = new WorkerContextBase();
        worker.Init(limit * 100, null, new MockMasterClock(), time);
        var blob = name.Contains(".Cryptonote.") || name.Contains(".Conceal.") || name.Contains(".Zano.");
        object issued;
        if(blob)
        {
            worker.RegisterBlobDifficulty(source, 1);
            issued = (source, 1u);
        }
        else
            issued = worker.JobForDifficulty(source, (_, _) => throw new InvalidOperationException(), "source");
        worker.SetDifficulty(limit * 200);
        time.AdvanceMonotonic(TimeSpan.FromHours(1));
        var proof = limit * (exceptionallyStrong ? 1000 : 2);
        Assert.Equal(limit, worker.ValidateShareDifficulty(proof, true, issued));
        if(!exceptionallyStrong)
            Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(proof, false, issued));
        // Retired ordinary work may satisfy the new target, earning its new credit.
        Assert.Equal(limit * 200, worker.ValidateShareDifficulty(limit * 1000, false, issued));
        worker.CloseDifficultyAssignments();
        Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(proof, true, issued));
        if(!blob)
        {
            worker.Init(1, null, new MockMasterClock(), time);
            Assert.Throws<StratumException>(() => worker.ValidateShareDifficulty(proof, true, issued));
        }
    }

    [Fact]
    public void CapDoesNotDependOnProofStrengthAndInvalidTargetsFailBeforeCredit()
    {
        Assert.Equal(3, CandidateShareCredit.Cap(3, 10));
        Assert.Equal(10, CandidateShareCredit.Cap(300, 10));
        foreach(var invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<System.IO.InvalidDataException>(() => CandidateShareCredit.Cap(1, invalid));
            Assert.Throws<System.IO.InvalidDataException>(() => CandidateShareCredit.Cap(invalid, 1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticCeilingBoundsPendingAndFallingTargetsWithoutWeakeningFloors(bool integer)
    {
        WorkerContextBase worker = integer ? new Miningcore.Blockchain.Cryptonote.CryptonoteWorkerContext() : new WorkerContextBase();
        worker.Init(100, new VarDiffConfig { MinDiff = 1, MaxDiff = 1000 }, new MockMasterClock());
        worker.EnqueueNewDifficulty(900);
        Assert.True(CandidateShareCredit.BoundAutomaticAssignment(worker, 20.5));
        worker.ApplyPendingDifficulty();
        Assert.Equal(integer ? 20 : 20.5, worker.Difficulty);
        Assert.True(CandidateShareCredit.BoundAutomaticAssignment(worker, 10));
        worker.ApplyPendingDifficulty();
        Assert.Equal(10, worker.Difficulty);
        worker.VarDiff.Config.MinDiff = 30;
        worker.EnqueueNewDifficulty(900);
        Assert.False(CandidateShareCredit.BoundAutomaticAssignment(worker, 5));
        Assert.False(worker.HasPendingDifficulty);
        Assert.Equal(10, worker.Difficulty);
        worker.VarDiff = null;
        worker.SetDifficulty(100);
        Assert.True(CandidateShareCredit.BoundAutomaticAssignment(worker, 5));
        Assert.False(worker.HasPendingDifficulty);
        Assert.Equal(100, worker.Difficulty);
    }

    [Fact]
    public void LegacyWarthogUsesConservativeUnitCreditUntilHashDomainIsAligned()
    {
        var job = RuntimeHelpers.GetUninitializedObject(typeof(WarthogJob));
        Assert.Equal(1, CandidateShareCredit.Limit(job));
    }

    [Theory]
    [InlineData("Xelis", 2)] [InlineData("Zano", 2)]
    [InlineData("Xelis", 0.5)] [InlineData("Zano", 0.5)]
    [InlineData("Xelis", 0)] [InlineData("Zano", 0)]
    public void IncoherentMultiplierContractsFailAtPoolStartup(string family, double multiplier)
    {
        var assembly = typeof(BitcoinJob).Assembly;
        var template = (CoinTemplate) Activator.CreateInstance(assembly.GetType($"Miningcore.Configuration.{family}CoinTemplate"));
        template.GetType().GetProperty("ShareMultiplier").SetValue(template, multiplier);
        var pool = (PoolBase) RuntimeHelpers.GetUninitializedObject(assembly.GetType($"Miningcore.Blockchain.{family}.{family}Pool"));
        var error = Assert.Throws<PoolStartupException>(() => pool.Configure(new PoolConfig { Id = "invalid-multiplier", Template = template }, new ClusterConfig()));
        Assert.Contains("shareMultiplier=1", error.Message);
    }
}
