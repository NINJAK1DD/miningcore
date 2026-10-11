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
        Set("blockTargetValue", uint256.Zero); // Every vector is an ordinary, non-block proof.
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
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(source, uint256.Zero);
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
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(old, candidateTarget);
        typeof(KaspaJob).GetProperty("blockTargetValue").SetValue(current, candidateTarget);
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

    private sealed class FixedProof : IHashAlgorithm
    {
        internal double Difficulty { get; set; }
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            result.Clear();
            (KaspaConstants.Diff1b * 4 / (long) Difficulty).ToByteArray(true, false).CopyTo(result);
        }
    }
}
