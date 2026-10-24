using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.IO;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Equihash.Custom.Veruscoin;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Equihash;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Util;
using NBitcoin;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ExactCandidateTargetTests
{
    private sealed class Solver : EquihashSolver
    { public override bool Verify(ReadOnlySpan<byte> header, ReadOnlySpan<byte> solution) => true; }
    // Skip unrelated coinbase construction while exercising the actual complete
    // Init paths, including target selection and policy initialization.
    private sealed class Eq : EquihashJob
    {
        internal uint256 Target => blockTargetValue;
        protected override void BuildCoinbase() { coinbaseInitial = Array.Empty<byte>(); coinbaseInitialHash = new byte[32]; }
    }
    private sealed class Verus : VeruscoinJob
    {
        internal uint256 Target => blockTargetValue;
        protected override void BuildCoinbase() { coinbaseInitial = Array.Empty<byte>(); coinbaseInitialHash = new byte[32]; }
    }

    [Theory]
    [InlineData("Equihash", null)]
    [InlineData("Verus", null)]
    [InlineData("Verus", "merged")]
    [InlineData("Verus", "mergeMine")]
    public void EquihashInitCachesExactSelectedTargetInsteadOfDisplayedDifficulty(string family, string merged)
    {
        var parent = uint256.Parse("0000000123456789abcdef000000000000000000000000000000000000000012");
        var mergedTarget = new Target(EquihashConstants.ZCashDiff1b / 32);
        var template = new Miningcore.Blockchain.Equihash.DaemonResponses.EquihashBlockTemplate
        {
            Target = parent.ToString(), Bits = "1d00ffff", PreviousBlockhash = new string('0',64),
            Height = 101, CurTime = 1_700_000_000, Version = 4,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
            MergedBits = merged == "merged" ? ((uint) mergedTarget).ToString("x8") : null,
            MergeMineBits = merged == "mergeMine" ? ((uint) mergedTarget).ToString("x8") : null
        };
        var coin = new EquihashCoinTemplate
        {
            Networks = new Dictionary<string, EquihashCoinTemplate.EquihashNetworkParams>
            { ["main"] = new() { Diff1 = EquihashConstants.ZCashDiff1b.ToString("x") } }
        };
        EquihashJob source = family == "Equihash" ? new Eq() : new Verus();
        source.Init(template, "init", new PoolConfig { Template = coin }, new ClusterConfig(), new MockMasterClock(),
            new KeyId(new byte[20]), Network.Main, new Solver());
        var selected = source is Eq eq ? eq.Target : ((Verus) source).Target;
        Assert.Equal(merged == null ? parent : mergedTarget.ToUInt256(), selected);
        var expected = (double) new BigRational(EquihashConstants.ZCashDiff1b, new BigInteger(selected.ToBytes(), isUnsigned: true));
        Assert.Equal(expected, CandidateShareCredit.Limit(source), 8);
        if(merged != null) Assert.NotEqual(source.Difficulty, expected);
        var worker = new WorkerContextBase();
        worker.Init(expected * 1000, null, new MockMasterClock());
        var issued = source.ForWorker(worker);
        Assert.Equal(expected, worker.ValidateShareDifficulty(expected, true, issued), 8);
    }

    private sealed class Alph(byte[] digest) : AlephiumJob
    { protected override void HashProof(ReadOnlySpan<byte> input, Span<byte> output) => digest.CopyTo(output); }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AlephiumFullWidthProofAtAndJustAboveCompactTargetKeepsExactBoundary(int offset)
    {
        var target = new Target(AlephiumConstants.Diff1Target / 32).ToUInt256();
        var targetInteger = new BigInteger(target.ToBytes(), isUnsigned: true);
        var proofInteger = targetInteger + (offset == 2 ? BigInteger.One << ((int) targetInteger.GetBitLength() - 25) : offset);
        var raw = proofInteger.ToByteArray(true, true);
        var proofDifficulty = (double) new BigRational(AlephiumConstants.Diff1Target, proofInteger);
        if(raw.Length < 32) { var padded = new byte[32]; raw.CopyTo(padded, 32 - raw.Length); raw = padded; }
        Assert.Equal(target, new Target(new BigInteger(raw, true, true)).ToUInt256());
        var (from, to) = AlephiumUtils.BlockChainIndex(raw);
        var job = new Alph(raw);
        job.Init(new AlephiumBlockTemplate { JobId = "precision", TargetBlob = (offset == 2 ? uint256.One : target).ToString(), HeaderBlob = "00", FromGroup = from, ToGroup = to });
        var limit = (double) new BigRational(AlephiumConstants.Diff1Target, targetInteger);
        var context = new AlephiumWorkerContext();
        context.Init(offset == 0 ? limit * 1000 : offset == 1 ? limit / 2 : (proofDifficulty + limit) / 2 / .99, null, new MockMasterClock());
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), new MockMasterClock(), "precision", false);
        worker.SetContext(context);
        var issued = job.ForWorker(context);
        var nonce = new string('0', AlephiumConstants.NonceLength * 2);
        if(offset == 2)
        {
            Assert.Equal(AlephiumStratumError.LowDifficultyShare, Assert.Throws<AlephiumStratumException>(() => issued.ProcessShare(worker, nonce)).Code);
            context.SetDifficulty(proofDifficulty);
            issued = job.ForWorker(context);
        }
        var share = issued.ProcessShare(worker, nonce);
        Assert.Equal(offset == 0, share.IsBlockCandidate);
        Assert.Equal((offset == 0 ? limit : offset == 1 ? limit / 2 : proofDifficulty) / AlephiumConstants.ShareMultiplier, share.Difficulty, 8);
        Assert.Equal(AlephiumStratumError.DuplicatedShare, Assert.Throws<AlephiumStratumException>(() => issued.ProcessShare(worker, nonce)).Code);
    }
}
