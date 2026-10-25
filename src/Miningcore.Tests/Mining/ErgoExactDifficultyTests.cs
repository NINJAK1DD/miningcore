using System;
using System.IO;
using System.Numerics;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Ergo;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Util;
using NBitcoin;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ErgoExactDifficultyTests
{
    private sealed class LinuxNativeFactAttribute : FactAttribute
    { public LinuxNativeFactAttribute() { if(!OperatingSystem.IsLinux()) Skip = "Autolykos uses native Blake2b"; } }

    // Leave Autolykos preparation, admission, policy, assignment and accounting
    // on their production paths; control only the final 256-bit proof digest.
    internal sealed class Proof(BigInteger digest) : ErgoJob
    {
        protected override void HashFinalProof(ReadOnlySpan<byte> input, Span<byte> output)
        {
            output.Clear();
            var bytes = digest.ToByteArray(isUnsigned: true, isBigEndian: true);
            bytes.CopyTo(output[(32 - bytes.Length)..]);
        }
    }

    internal static readonly BigInteger Target = new(Convert.FromHexString("0000000080abcd" + new string('0', 48) + "12"), true, true);
    internal static double Exact(BigInteger target) => (double) new BigRational(BitcoinConstants.Diff1, target);

    internal static Share Admit(BigInteger target, BigInteger proof, double assignment, string id)
    {
        var source = new Proof(proof);
        source.Init(new WorkMessage { Msg = new string('0', 64), Height = 1, B = target }, 2, 4, id);
        var context = new ErgoWorkerContext { ExtraNonce1 = "abcdef01" };
        context.Init(assignment, null, new MockMasterClock());
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), new MockMasterClock(), id, true);
        worker.SetContext(context);
        return source.ForWorker(context).ProcessShare(worker, "unused", "1", "abcdef0100000001");
    }

    [Fact]
    public void InitPreservesExactTargetDifficultyAndRejectsMalformedTargetsBeforePublication()
    {
        var source = new ErgoJob();
        source.Init(new WorkMessage { Msg = new string('0', 64), Height = 1, B = Target }, 2, 4, "init");
        Assert.Equal(Exact(Target), source.Difficulty);
        Assert.Equal(source.Difficulty, CandidateShareCredit.Limit(source));
        Assert.True(new Target(Target).Difficulty > source.Difficulty);
        foreach(var invalid in new[] { BigInteger.Zero, -BigInteger.One, BigInteger.One << 256 })
            Assert.Throws<InvalidDataException>(() => new ErgoJob().Init(new WorkMessage { B = invalid }, 2, 4, "invalid"));
        var maximum = new ErgoJob();
        maximum.Init(new WorkMessage { B = (BigInteger.One << 256) - 1 }, 2, 4, "maximum");
        Assert.Equal(Exact((BigInteger.One << 256) - 1), maximum.Difficulty);
    }

    [LinuxNativeFact]
    public void FullWidthProofAndStrictCandidateBoundaryUseExactDifficulty()
    {
        var limit = Exact(Target);
        foreach(var offset in new[] { -1, 0, 1 })
        {
            var result = Admit(Target, Target + offset, offset < 0 ? limit * 200 : limit, "boundary");
            Assert.Equal(offset < 0, result.IsBlockCandidate);
            Assert.Equal(limit / ErgoConstants.ShareMultiplier, result.Difficulty);
            Assert.Equal(limit, result.NetworkDifficulty);
        }
    }

    [LinuxNativeFact]
    public void RoundedProofCannotCrossOrdinaryShareAdmissionTolerance()
    {
        var proof = Target * 3;
        var exact = Exact(proof);
        var rounded = new Target(proof).Difficulty;
        Assert.True(rounded > exact);
        var assignment = (exact + rounded) / 2 / .99;
        Assert.Equal(StratumError.LowDifficultyShare, Assert.Throws<StratumException>(() => Admit(Target, proof, assignment, "precision")).Code);
        var accepted = Admit(Target, proof, exact / .99, "exact");
        Assert.False(accepted.IsBlockCandidate);
        Assert.Equal(exact / .99 / ErgoConstants.ShareMultiplier, accepted.Difficulty);
    }

    [LinuxNativeFact]
    public void CandidateCreditUsesExactCachedTargetEvenAfterTemplateMutationAndRetarget()
    {
        var template = new WorkMessage { Msg = new string('0', 64), Height = 1, B = Target };
        var source = new Proof(Target - 1);
        source.Init(template, 2, 4, "snapshot");
        var context = new ErgoWorkerContext { ExtraNonce1 = "abcdef01" };
        context.Init(Exact(Target) * 200, null, new MockMasterClock());
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), new MockMasterClock(), "snapshot", true);
        worker.SetContext(context);
        var issued = source.ForWorker(context);
        context.SetDifficulty(Exact(Target) * 400);
        template.B = BigInteger.One;
        var result = issued.ProcessShare(worker, "unused", "1", "abcdef0100000001");
        Assert.True(result.IsBlockCandidate);
        Assert.Equal(Exact(Target), result.NetworkDifficulty);
        Assert.Equal(Exact(Target) / ErgoConstants.ShareMultiplier, result.Difficulty);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() =>
            source.ForWorker(context).ProcessShare(worker, "unused", "2", "ABCDEF0100000001")).Code);
        var zero = Admit(Target, BigInteger.Zero, Exact(Target) * 200, "zero");
        Assert.True(zero.IsBlockCandidate);
        Assert.Equal(Exact(Target) / ErgoConstants.ShareMultiplier, zero.Difficulty);
        var lower = Admit(Target, BigInteger.One, Exact(Target) / 2, "lower");
        Assert.Equal(Exact(Target) / 2 / ErgoConstants.ShareMultiplier, lower.Difficulty);
    }
}
