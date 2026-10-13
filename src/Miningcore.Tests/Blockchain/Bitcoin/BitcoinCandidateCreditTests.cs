using System;
using System.Numerics;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using NLog;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinCandidateCreditTests : TestBase
{
    [Theory]
    [InlineData(false, "1d00ffff", "1d00ffff", 2, 1)]
    [InlineData(false, "1d00ffff", "1d00ffff", 2000, 65536)]
    [InlineData(true, "1d00ffff", "1c00ffff", 2, 1)]
    [InlineData(true, "1c00ffff", "1d00ffff", 2, 1)]
    [InlineData(true, "1d00ffff", "1d00ffff", 2000, 65536)]
    public void CandidateCredit_UsesQualifyingChainTargetEvenForExceptionallyStrongProofs(
        bool merged, string parentBits, string auxBits, int proofDifficulty, double multiplier)
    {
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1_700_000_000) };
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["bitcoin"];
        var template = new BlockTemplate
        {
            Height = 101, Version = 0x20000000, CurTime = 1_700_000_000,
            PreviousBlockhash = new string('0', 64), CoinbaseValue = 5_000_000_000,
            Bits = parentBits, Target = new Target(Convert.ToUInt32(parentBits, 16)).ToUInt256().ToString(),
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
        };
        BitcoinJob source;
        var config = new PoolConfig { Template = coin };
        if(merged)
        {
            var job = new MergedMiningBitcoinJob();
            job.InitMerged(template, new AuxBlockTemplate { Hash = new string('1', 64), Bits = auxBits, Height = 101 },
                "candidate", config, null, new ClusterConfig(), clock, new KeyId(new byte[20]), Network.RegTest,
                false, multiplier, new Sha256D(), new FixedProof(proofDifficulty), new Sha256D());
            source = job;
        }
        else
        {
            source = new BitcoinJob();
            source.Init(template, "candidate", config, null, new ClusterConfig(), clock, new KeyId(new byte[20]),
                Network.RegTest, false, multiplier, new Sha256D(), new FixedProof(proofDifficulty), new Sha256D());
        }
        BitcoinWorkerContext context = merged ? new MergedMiningBitcoinWorkerContext() : new BitcoinWorkerContext();
        context.ConfigureDifficultyPolicy(multiplier);
        context.Init(1000 * multiplier, null, clock);
        context.ExtraNonce1 = "60000001";
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(), clock, "candidate-credit", false);
        worker.SetContext(context);
        var issued = source.ForWorker(context);
        var share = merged
            ? ((MergedMiningBitcoinJob) issued).ProcessShareMerged(worker, "10000000000000", "6553f100", "00000001").Share
            : issued.ProcessShare(worker, "10000000000000", "6553f100", "00000001").Share;
        Assert.Equal(1, share.Difficulty);
        Assert.Equal(proofDifficulty * multiplier, share.ShareDifficulty, 8);
        Assert.InRange(PpsArithmetic.Calculate(5_000_000_000, share.Difficulty, share.NetworkDifficulty, 100),
            0.00000001m, 50m);
        // The same accepted proof cannot be repriced under another assignment ID.
        context.SetDifficulty(2000 * multiplier);
        var replacement = source.ForWorker(context);
        var duplicate = Assert.Throws<StratumException>(() =>
        {
            if(merged) ((MergedMiningBitcoinJob) replacement).ProcessShareMerged(worker, "10000000000000", "6553f100", "00000001");
            else replacement.ProcessShare(worker, "10000000000000", "6553f100", "00000001");
        });
        Assert.Equal(StratumError.DuplicateShare, duplicate.Code);
    }

    private sealed class FixedProof(int difficulty) : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            result.Clear();
            (BitcoinConstants.Diff1 / difficulty).ToByteArray(true, false).CopyTo(result);
        }
    }

    [Fact]
    public void WorkerPolicy_ValidatesEveryProducerAndResetsMinimumForNewSessions()
    {
        var context = new BitcoinWorkerContext();
        context.ConfigureDifficultyPolicy(65536);
        var clock = new MockMasterClock();
        context.Init(1, null, clock);
        context.EnqueueNewDifficulty(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => context.SetDifficulty(1e100));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.EnqueueNewDifficulty(1e100));
        Assert.Equal(1, context.Difficulty);
        Assert.True(context.HasPendingDifficulty);
        context.SetMinimumDifficulty(8);
        context.SetDifficulty(2);
        context.EnqueueNewDifficulty(2);
        context.ApplyPendingDifficulty();
        Assert.Equal(8, context.Difficulty);
        var old = context.JobForDifficulty(new object(), (_, _) => new object(), "policy");
        context.Init(1, null, clock);
        Assert.Null(context.NegotiatedMinimumDifficulty);
        Assert.Equal(1, context.Difficulty);
        Assert.False(context.TryValidateShareDifficulty(100, true, old, out _));
    }
}
