using System;
using System.Globalization;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using NLog;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningDifficultyGraceTests : TestBase
{
    private sealed class CustomLayoutJob(int placeholderBytes) : MergedMiningBitcoinJob
    {
        protected override Script GenerateScriptSigInitial()
        {
            extraNoncePlaceHolderLength = placeholderBytes;
            return base.GenerateScriptSigInitial();
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void MergedExtranonceWidthFollowsSerializedJobLayout(int placeholderBytes)
    {
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["litecoin"];
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1_700_000_000) };
        var template = new BlockTemplate
        {
            Height = 101, Version = 0x20000000, CurTime = 1_700_000_000,
            PreviousBlockhash = new string('0', 64), CoinbaseValue = 5_000_000_000,
            Bits = "1b010000", Target = new Target(0x1b010000).ToUInt256().ToString(),
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
        };
        var source = new CustomLayoutJob(placeholderBytes);
        source.InitMerged(template, new AuxBlockTemplate { Hash = new string('1', 64), Bits = "1b010000", Height = 101 },
            "layout", new PoolConfig { Template = coin }, null, new ClusterConfig(), clock,
            new KeyId(new byte[20]), Network.RegTest, false, 1, new Sha256D(), new FixedProof(), new Sha256D());
        var context = new MergedMiningBitcoinWorkerContext { ExtraNonce1 = "60000001" };
        context.Init(10, null, clock);
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(), clock, "merged-layout", false);
        connection.SetContext(context);
        var job = (MergedMiningBitcoinJob) source.ForWorker(context);
        MergedMiningShareResult Submit(string en2) => job.ProcessShareMerged(connection, en2, template.CurTime.ToStringHex8(), "00000001");
        Assert.Equal("incorrect size of extraNonce2", Assert.Throws<StratumException>(() => Submit(new string('0', 14))).Message);
        var valid = "ab" + new string('0', placeholderBytes * 2 - context.ExtraNonce1.Length - 2);
        Assert.Equal(10, Submit(valid).Share.Difficulty);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => Submit(valid.ToUpperInvariant())).Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ParentAndAuxiliaryProofsKeepIssuedCreditDuplicateAndExpiryContracts(bool parentCandidate, bool auxiliaryCandidate)
    {
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["litecoin"];
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1_700_000_000) };
        var time = new ManualTimeProvider();
        var parentBits = parentCandidate ? "207fffff" : "1b010000";
        var auxiliaryBits = auxiliaryCandidate ? "207fffff" : "1b010000";
        var template = new BlockTemplate
        {
            Height = 101, Version = 0x20000000, CurTime = 1_700_000_000,
            PreviousBlockhash = new string('0', 64), CoinbaseValue = 5_000_000_000,
            Bits = parentBits, Target = new Target(uint.Parse(parentBits, NumberStyles.HexNumber)).ToUInt256().ToString(),
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
        };
        var source = new MergedMiningBitcoinJob();
        source.InitMerged(template, new AuxBlockTemplate { Hash = new string('1', 64), Bits = auxiliaryBits, Height = 101 },
            "merged", new PoolConfig { Template = coin }, null, new ClusterConfig(), clock,
            new KeyId(new byte[20]), Network.RegTest, false, 1, new Sha256D(), new FixedProof(), new Sha256D());
        var context = new MergedMiningBitcoinWorkerContext { ExtraNonce1 = "60000001" };
        context.Init(10, null, clock, time);
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(), clock, "merged-difficulty", false);
        connection.SetContext(context);
        var old = (MergedMiningBitcoinJob) source.ForWorker(context);
        context.AddJob(old, 4);
        context.SetDifficulty(40);
        var current = (MergedMiningBitcoinJob) source.ForWorker(context);
        context.AddJob(current, 4);
        MergedMiningShareResult Submit(MergedMiningBitcoinJob job, string nonce) => job.ProcessShareMerged(connection,
            "10000000000000", template.CurTime.ToStringHex8(), nonce, null);
        var accepted = Submit(old, "00000001");
        Assert.Equal(10, accepted.Share.Difficulty);
        Assert.Equal(parentCandidate, accepted.Share.IsBlockCandidate);
        Assert.Equal(auxiliaryCandidate, accepted.AuxPowHex != null);
        foreach(var alias in new[] { "0g000000000000", "0x10000000000000", "10000000000000f", "10000000000000z" })
            Assert.Equal(StratumError.Other, Assert.Throws<StratumException>(() => old.ProcessShareMerged(connection,
                alias, template.CurTime.ToStringHex8(), "00000001", null)).Code);
        Assert.Equal(StratumError.Other, Assert.Throws<StratumException>(() => old.ProcessShareMerged(connection,
            "10000000000000", template.CurTime.ToStringHex8(), " 0000001", null)).Code);
        Assert.Equal(StratumError.Other, Assert.Throws<StratumException>(() => old.ProcessShareMerged(connection,
            "10000000000000", " " + template.CurTime.ToStringHex8()[1..], "00000001", null)).Code);
        Assert.Equal((int) StratumError.DuplicateShare,
            (int) Assert.Throws<StratumException>(() => Submit(current, "00000001")).Code);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        if(parentCandidate || auxiliaryCandidate)
        {
            Assert.Equal(10, Submit(old, "00000002").Share.Difficulty);
            Assert.Equal(40, Submit(current, "00000003").Share.Difficulty);
        }
        else
        {
            Assert.Throws<StratumException>(() => Submit(old, "00000002"));
            Assert.Throws<StratumException>(() => Submit(current, "00000003"));
        }
        context.AddJob(source.ForWorker(context), 1);
        Assert.Null(context.GetJob(old.JobId)); // Grace never overrides registry age/eviction.
    }

    private sealed class FixedProof : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        {
            result.Clear();
            (BitcoinConstants.Diff1 / 20).ToByteArray(true, false).CopyTo(result);
        }
    }
}
