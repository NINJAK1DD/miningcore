using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.IO;
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
        Assert.Equal(StratumError.LowDifficultyShare,
            Assert.Throws<StratumException>(() => retired.ProcessShare(connection, "0000000000000003")).Code);
        Assert.Equal(1000 / multiplier, current.ProcessShare(connection, "0000000000000004").Difficulty);
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
