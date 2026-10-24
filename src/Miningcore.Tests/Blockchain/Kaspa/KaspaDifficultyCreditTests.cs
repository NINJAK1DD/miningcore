using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Kaspa;
using Miningcore.Blockchain.Kaspa.Custom.Astrix;
using Miningcore.Blockchain.Kaspa.Custom.Karlsencoin;
using Miningcore.Blockchain.Kaspa.Custom.Spectre;
using Miningcore.Blockchain.Kaspa.Custom.WagLayla;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using NLog;
using Xunit;
using Kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Tests.Blockchain.Kaspa;

public class KaspaDifficultyCreditTests
{
    private sealed class LinuxNativeTheoryAttribute : TheoryAttribute
    {
        public LinuxNativeTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux()) Skip = "Custom Kaspa validators require the Linux native hashing library";
        }
    }

    private static readonly Type[] JobTypes =
        { typeof(KaspaJob), typeof(AstrixJob), typeof(KarlsencoinJob), typeof(SpectreJob), typeof(WagLaylaJob) };
    public static IEnumerable<object[]> Validators => JobTypes
        .SelectMany(type => new[] { new object[] { type.Name, 2d }, new object[] { type.Name, 2000d } });

    [LinuxNativeTheory]
    [MemberData(nameof(Validators))]
    public void ProductionValidator_CreditsIssuedAssignmentAndExpiresOnlyRetiredWork(string validator, double proofDifficulty)
    {
        const double multiplier = 4;
        var time = new ManualTimeProvider();
        var clock = new MockMasterClock();
        var context = new KaspaWorkerContext { ExtraNonce1 = "00000001" };
        context.Init(1, null, clock, time);
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "kaspa-credit", false);
        connection.SetContext(context);
        // Exercise each concrete production validator and its Share mapping. Only
        // the final hash is fixed; these are accounting vectors, not consensus proofs.
        var hash = new FixedProof { Difficulty = proofDifficulty };
        var source = (KaspaJob) Activator.CreateInstance(JobTypes.Single(type => type.Name == validator), new Sha256D(), new Sha256D(), hash);
        void Set(string name, object value) => typeof(KaspaJob).GetProperty(name).SetValue(source, value);
        Set("JobId", "template");
        Set("BlockTemplate", new Kaspad.RpcBlock { Header = new Kaspad.RpcBlockHeader { DaaScore = 101, Timestamp = 123 } });
        Set("prePowHashBytes", Enumerable.Range(1, 32).Select(x => (byte) x).ToArray());
        Set("blockTargetValue", uint256.One); // Every vector is an ordinary, non-block proof.
        typeof(KaspaJob).GetField("shareMultiplier", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, multiplier);
        var retired = source.ForWorker(context);
        context.SetDifficulty(1000);
        var current = source.ForWorker(context);
        var share = retired.ProcessShare(connection, "0000000000000001");
        Assert.False(share.IsBlockCandidate);
        Assert.Equal(1 / multiplier, share.Difficulty);
        Assert.Equal(101, share.BlockHeight);
        Assert.Equal(StratumError.DuplicateShare,
            Assert.Throws<StratumException>(() => current.ProcessShare(connection, "0000000000000001")).Code);
        if(proofDifficulty < 1000)
            Assert.Equal(StratumError.LowDifficultyShare,
                Assert.Throws<StratumException>(() => current.ProcessShare(connection, "0000000000000002")).Code);
        else
            Assert.Equal(1000 / multiplier, current.ProcessShare(connection, "0000000000000002").Difficulty);
        time.MoveWallClock(TimeSpan.FromDays(-1000));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        hash.Difficulty = 2000;
        Assert.Equal(1000 / multiplier, retired.ProcessShare(connection, "0000000000000003").Difficulty);
        Assert.Equal(1000 / multiplier, current.ProcessShare(connection, "0000000000000004").Difficulty);
        Assert.Equal(0ul, source.BlockTemplate.Header.Nonce);
    }

    [LinuxNativeTheory]
    [InlineData("KaspaJob")]
    [InlineData("AstrixJob")]
    [InlineData("KarlsencoinJob")]
    [InlineData("SpectreJob")]
    [InlineData("WagLaylaJob")]
    public void RejectedProofCanRecoverAtNewAssignmentAndConcurrentAliasesCommitExactlyOnce(string validator)
    {
        var clock = new MockMasterClock();
        var time = new ManualTimeProvider();
        var context = new KaspaWorkerContext { ExtraNonce1 = "00000001" };
        context.Init(100, null, clock, time);
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "kaspa-retry", false);
        connection.SetContext(context);
        var hash = new FixedProof { Difficulty = 20 };
        var source = (KaspaJob) Activator.CreateInstance(JobTypes.Single(type => type.Name == validator), new Sha256D(), new Sha256D(), hash);
        typeof(KaspaJob).GetProperty("JobId").SetValue(source, "1");
        typeof(KaspaJob).GetProperty("BlockTemplate").SetValue(source,
            new Kaspad.RpcBlock { Header = new Kaspad.RpcBlockHeader { DaaScore = 101, Timestamp = 123 } });
        typeof(KaspaJob).GetProperty("prePowHashBytes").SetValue(source, Enumerable.Range(1, 32).Select(x => (byte) x).ToArray());
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(source, uint256.One);
        typeof(KaspaJob).GetField("shareMultiplier", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, 4d);
        var old = source.ForWorker(context);
        Assert.Equal(StratumError.LowDifficultyShare,
            Assert.Throws<StratumException>(() => old.ProcessShare(connection, "0000000000000001")).Code);
        context.SetDifficulty(10);
        var current = source.ForWorker(context, () => "2");
        Assert.Equal(10 / 4d, current.ProcessShare(connection, "0000000000000001").Difficulty);
        Assert.Equal(StratumError.DuplicateShare,
            Assert.Throws<StratumException>(() => old.ProcessShare(connection, "0000000000000001")).Code);
        hash.Difficulty = 2000;
        var accepted = new ConcurrentBag<Share>();
        var rejected = new ConcurrentBag<StratumError>();
        Parallel.For(0, 32, i =>
        {
            try { accepted.Add((i % 2 == 0 ? old : current).ProcessShare(connection, "0000000000000002")); }
            catch(StratumException failure) { rejected.Add(failure.Code); }
        });
        Assert.Single(accepted);
        Assert.Equal(31, rejected.Count);
        Assert.All(rejected, error => Assert.Equal(StratumError.DuplicateShare, error));
        Assert.Equal(0ul, source.BlockTemplate.Header.Nonce);

        // Simultaneous distinct candidates must carry their own exact nonce,
        // including custom validators, without mutating the shared template.
        var candidateTarget = uint256.Parse(new string('f', 64));
        // A fresh template owns the new target; never mutate already issued work.
        var fresh = (KaspaJob) typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null);
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(fresh, candidateTarget);
        Miningcore.Mining.CandidateShareCredit.Initialize(fresh);
        old = fresh.ForWorker(context, () => "3");
        context.SetDifficulty(11);
        current = fresh.ForWorker(context, () => "4");
        Parallel.For(100, 116, value =>
        {
            var candidate = (value % 2 == 0 ? old : current).ProcessShare(connection, value.ToString("x16"));
            Assert.True(candidate.IsBlockCandidate);
            var block = source.GetBlockCandidate(candidate);
            Assert.NotSame(source.BlockTemplate, block);
            Assert.Equal((ulong) value, block.Header.Nonce);
            Assert.Equal(101ul, block.Header.DaaScore);
        });
        Assert.Equal(0ul, source.BlockTemplate.Header.Nonce);
    }

    [LinuxNativeTheory]
    [InlineData("KaspaJob")]
    [InlineData("AstrixJob")]
    [InlineData("KarlsencoinJob")]
    [InlineData("SpectreJob")]
    [InlineData("WagLaylaJob")]
    public void EquivalentEffectiveNonceEncodingsCannotCreateAnotherCredit(string validator)
    {
        var clock = new MockMasterClock();
        var context = new KaspaWorkerContext { ExtraNonce1 = "00000000" };
        context.Init(100, null, clock);
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "kaspa-encoding", true);
        connection.SetContext(context);
        var hash = new FixedProof { Difficulty = 20 };
        var source = (KaspaJob) Activator.CreateInstance(JobTypes.Single(type => type.Name == validator), new Sha256D(), new Sha256D(), hash);
        typeof(KaspaJob).GetProperty("JobId").SetValue(source, "1");
        typeof(KaspaJob).GetProperty("BlockTemplate").SetValue(source,
            new Kaspad.RpcBlock { Header = new Kaspad.RpcBlockHeader { DaaScore = 101, Timestamp = 123 } });
        typeof(KaspaJob).GetProperty("prePowHashBytes").SetValue(source, Enumerable.Range(1, 32).Select(x => (byte) x).ToArray());
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(source, uint256.One);
        typeof(KaspaJob).GetField("shareMultiplier", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, 4d);
        var old = source.ForWorker(context);
        Assert.Equal(StratumError.LowDifficultyShare,
            Assert.Throws<StratumException>(() => old.ProcessShare(connection, "0x1")).Code);
        context.SetDifficulty(10);
        var current = source.ForWorker(context, () => "2");
        Assert.Equal(10 / 4d, current.ProcessShare(connection, "0000000000000001").Difficulty);
        foreach(var nonce in new[] { "1", "0x01", "000000001", "0X0000000000000001" })
            Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => old.ProcessShare(connection, nonce)).Code);

        var credits = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<StratumError>();
        Parallel.For(0, 32, i =>
        {
            try { credits.Add(current.ProcessShare(connection, i % 2 == 0 ? "0xA" : "000000000000000a").Difficulty); }
            catch(StratumException ex) { failures.Add(ex.Code); }
        });
        Assert.Single(credits);
        Assert.Equal(31, failures.Count);
        Assert.All(failures, code => Assert.Equal(StratumError.DuplicateShare, code));
        Assert.Equal(10 / 4d, credits.Sum());
        Assert.Equal(0ul, source.BlockTemplate.Header.Nonce);
        foreach(var nonce in new[] { "0x", "g", "-1", "10000000000000000", " 1" })
            Assert.Equal(StratumError.Other, Assert.Throws<StratumException>(() => current.ProcessShare(connection, nonce)).Code);
    }

    [LinuxNativeTheory]
    [InlineData("abcd")]
    [InlineData("abcd0001")]
    [InlineData("abcd00011234")]
    public void ShortNonceProjectionUsesPrefixPlusLeftPaddedMinerTail(string extraNonce)
    {
        var clock = new MockMasterClock();
        var context = new KaspaWorkerContext { ExtraNonce1 = extraNonce };
        context.Init(10, null, clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), clock, "kaspa-padding", true);
        worker.SetContext(context);
        var source = new KaspaJob(new Sha256D(), new Sha256D(), new FixedProof { Difficulty = 20 });
        typeof(KaspaJob).GetProperty("JobId").SetValue(source, "1");
        typeof(KaspaJob).GetProperty("BlockTemplate").SetValue(source, new Kaspad.RpcBlock { Header = new Kaspad.RpcBlockHeader { DaaScore = 101, Timestamp = 123 } });
        typeof(KaspaJob).GetProperty("prePowHashBytes").SetValue(source, Enumerable.Range(1, 32).Select(x => (byte) x).ToArray());
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(source, uint256.Parse(new string('f', 64)));
        typeof(KaspaJob).GetField("shareMultiplier", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, 4d);
        var job = source.ForWorker(context);
        var tail = "1".PadLeft(16 - extraNonce.Length, '0');
        var full = extraNonce + tail;
        var accepted = job.ProcessShare(worker, "1");
        Assert.Equal(Convert.ToUInt64(full, 16), source.GetBlockCandidate(accepted).Header.Nonce);
        foreach(var alias in new[] { tail, "0x" + tail, full.ToUpperInvariant(), "0X" + full })
            Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => job.ProcessShare(worker, alias)).Code);
        Assert.Equal(0ul, source.BlockTemplate.Header.Nonce);
    }

    private sealed class FixedProof : IHashAlgorithm
    {
        internal double Difficulty { get; set; }
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            result.Clear();
            (KaspaConstants.Diff1b * 4 / (long) Difficulty).ToByteArray(true, false).CopyTo(result);
        }
    }

    [LinuxNativeTheory]
    [MemberData(nameof(Validators))]
    public void CandidateCreditUsesHashTargetAndMultiplierForEveryValidator(string validator, double proofDifficulty)
    {
        var clock = new MockMasterClock();
        var context = new KaspaWorkerContext { ExtraNonce1 = "00000001" };
        context.Init(4000, null, clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), clock, "candidate", false);
        worker.SetContext(context);
        var source = (KaspaJob) Activator.CreateInstance(JobTypes.Single(type => type.Name == validator), new Sha256D(), new Sha256D(), new FixedProof { Difficulty = proofDifficulty * 4 });
        typeof(KaspaJob).GetProperty("JobId").SetValue(source, "1");
        typeof(KaspaJob).GetProperty("BlockTemplate").SetValue(source, new Kaspad.RpcBlock { Header = new Kaspad.RpcBlockHeader { DaaScore = 101, Timestamp = 123 } });
        typeof(KaspaJob).GetProperty("prePowHashBytes").SetValue(source, Enumerable.Range(1, 32).Select(x => (byte) x).ToArray());
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(source, new Target(KaspaConstants.Diff1b).ToUInt256());
        typeof(KaspaJob).GetField("shareMultiplier", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, 4d);
        var old = source.ForWorker(context);
        context.SetDifficulty(8000);
        var accepted = old.ProcessShare(worker, "0000000000000001");
        Assert.True(accepted.IsBlockCandidate);
        Assert.Equal(1, accepted.Difficulty);
        Assert.Equal(1ul, source.GetBlockCandidate(accepted).Header.Nonce);
        var current = source.ForWorker(context);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => current.ProcessShare(worker, "0000000000000001")).Code);
    }
}
