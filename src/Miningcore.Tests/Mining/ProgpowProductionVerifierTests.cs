using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Progpow;
using Miningcore.Blockchain.Progpow.Custom.Firo;
using Miningcore.Blockchain.Progpow.Custom.Kiiro;
using Miningcore.Blockchain.Progpow.Custom.Realichain;
using Miningcore.Blockchain.Progpow.Custom.Telestai;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Time;
using NBitcoin;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

// Use the production verifier, coinbase/header serialization, hash comparisons,
// assignment and duplicate commit. Only the final algorithm cache is controlled;
// these are protocol/credit regressions, not KawPoW/FiroPoW consensus vectors.
public class ProgpowProductionVerifierTests : TestBase
{
    public static IEnumerable<object[]> Cases => new[] { "Progpow", "Firo", "Kiiro", "Realichain", "Telestai" }
        .SelectMany(family => new[] { new object[] { family, true, false }, new object[] { family, false, true }, new object[] { family, true, true } });

    internal sealed class Cache(string family) : IProgpowCache
    {
        internal double ProofDifficulty = 20;
        internal int Calls;
        internal ulong LastNonce;
        internal byte[] LastHeader;
        internal static string Mix => new string('a', 64);
        public byte[] SeedHash { get; set; } = new byte[32];
        public bool Compute(ILogger logger, int blockNumber, byte[] header, ulong nonce, out byte[] mix, out byte[] result)
        {
            Calls++;
            LastNonce = nonce;
            LastHeader = (byte[]) header.Clone();
            var diff1 = family switch { "Progpow" => RavencoinConstants.Diff1, "Telestai" => TelestaiConstants.Diff1, _ => FiroConstants.Diff1 };
            mix = Convert.FromHexString(Mix);
            result = (diff1 / (BigInteger) ProofDifficulty).ToByteArray(true, true).PadFront(0, 32);
            return true;
        }
        public void Dispose() { }
    }

    internal static ProgpowJob CreateSource(string family, IMasterClock clock, Cache cache, string target = null)
    {
        ProgpowJob source = family switch
        {
            "Firo" => new FiroJob(), "Kiiro" => new KiiroJob(), "Realichain" => new RealichainJob(),
            "Telestai" => new TelestaiJob(), _ => new ProgpowJob()
        };
        var coin = (ProgpowCoinTemplate) ModuleInitializer.CoinTemplates["ravencoin"];
        source.Init(new BlockTemplate
        {
            Version = 0x20000000, PreviousBlockhash = new string('0', 64), Height = 101,
            CurTime = (uint) ((DateTimeOffset) clock.Now).ToUnixTimeSeconds(), CoinbaseValue = 5_000_000_000,
            Bits = "1b010000", Target = target ?? new string('0', 63) + "1", Transactions = Array.Empty<BitcoinBlockTransaction>()
        }, "production", new PoolConfig { Template = coin }, null, new ClusterConfig(), clock,
            new KeyId(new byte[20]), Network.RegTest, false, 1, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
            coin.BlockHasherValue, cache);
        return source;
    }

    private static (StratumConnection Worker, ProgpowJob Source, ProgpowWorkerJob Job, Cache Cache, string Header) Create(string family)
    {
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1_700_000_000) };
        var cache = new Cache(family);
        var context = new ProgpowWorkerContext { ExtraNonce1 = "abcdef01" };
        context.Init(10, null, clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), clock, "production", true);
        worker.SetContext(context);
        var source = CreateSource(family, clock, cache);
        var job = new ProgpowWorkerJob("1", context.ExtraNonce1);
        ((ProgpowJob) source.ForWorker(context, preserveId: true)).PrepareWorkerJob(job, out var header);
        Assert.NotEqual(header, header.ToUpperInvariant()); // Exercise letter case, not only digits.
        return (worker, source, job, cache, header);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void UppercaseFirstProofPassesProductionComparisonsAndHasOnlyOneCredit(string family, bool uppercaseHeader, bool uppercaseMix)
    {
        var (worker, source, job, cache, header) = Create(family);
        var accepted = job.ProcessShare(LogManager.GetCurrentClassLogger(), worker, "ABCDEF010000000A",
            uppercaseHeader ? header.ToUpperInvariant() : header, uppercaseMix ? Cache.Mix.ToUpperInvariant() : Cache.Mix).Share;
        Assert.False(accepted.IsBlockCandidate);
        Assert.Equal(10, accepted.Difficulty);
        Assert.Equal(1, cache.Calls);
        Assert.Equal(0xabcdef010000000aUL, cache.LastNonce);
        Assert.Equal(Convert.FromHexString(header), cache.LastHeader);
        worker.Context.SetDifficulty(40);
        var current = new ProgpowWorkerJob("2", "abcdef01");
        ((ProgpowJob) source.ForWorker(worker.Context, preserveId: true)).PrepareWorkerJob(current, out var sameHeader);
        Assert.Equal(header, sameHeader);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() =>
            current.ProcessShare(LogManager.GetCurrentClassLogger(), worker, "abcdef010000000a", header, Cache.Mix)).Code);
        Assert.Equal(1, cache.Calls);
    }

    [Theory]
    [InlineData("Progpow")]
    [InlineData("Firo")]
    [InlineData("Kiiro")]
    [InlineData("Realichain")]
    [InlineData("Telestai")]
    public void IncorrectHashesRemainRejectedAndDoNotPoisonRetry(string family)
    {
        var (worker, _, job, cache, header) = Create(family);
        Assert.Equal("bad header-hash", Assert.Throws<StratumException>(() => job.ProcessShare(LogManager.GetCurrentClassLogger(), worker,
            "abcdef010000000a", new string('0', 64), Cache.Mix)).Message);
        Assert.Equal(0, cache.Calls);
        Assert.Equal("bad mix-hash", Assert.Throws<StratumException>(() => job.ProcessShare(LogManager.GetCurrentClassLogger(), worker,
            "abcdef010000000a", header, new string('0', 64))).Message);
        Assert.Equal(1, cache.Calls);
        Assert.Equal(10, job.ProcessShare(LogManager.GetCurrentClassLogger(), worker, "ABCDEF010000000A",
            header.ToUpperInvariant(), Cache.Mix.ToUpperInvariant()).Share.Difficulty);
        Assert.Equal(2, cache.Calls);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => job.ProcessShare(LogManager.GetCurrentClassLogger(), worker,
            "abcdef010000000a", header, Cache.Mix)).Code);
        Assert.Equal(2, cache.Calls);
    }

    [Theory]
    [InlineData("Progpow", 20)] [InlineData("Progpow", 2000)]
    [InlineData("Firo", 20)] [InlineData("Firo", 2000)]
    [InlineData("Kiiro", 20)] [InlineData("Kiiro", 2000)]
    [InlineData("Realichain", 20)] [InlineData("Realichain", 2000)]
    [InlineData("Telestai", 20)] [InlineData("Telestai", 2000)]
    public void CandidateCreditUsesExactFamilyTargetAndCannotBeRepriced(string family, double proof)
    {
        var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddSeconds(1_700_000_000) };
        var cache = new Cache(family) { ProofDifficulty = proof };
        var numerator = family is "Progpow" or "Telestai" ? RavencoinConstants.Diff1 : FiroConstants.Diff1;
        var source = CreateSource(family, clock, cache, new Target(numerator).ToUInt256().ToString());
        var context = new ProgpowWorkerContext { ExtraNonce1 = "abcdef01" };
        context.Init(1000, null, clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), clock, "candidate", true);
        worker.SetContext(context);
        var old = new ProgpowWorkerJob("1", context.ExtraNonce1);
        ((ProgpowJob) source.ForWorker(context, preserveId: true)).PrepareWorkerJob(old, out var header);
        context.SetDifficulty(4000);
        var accepted = old.ProcessShare(LogManager.GetCurrentClassLogger(), worker, "abcdef0100000001", header, Cache.Mix).Share;
        Assert.True(accepted.IsBlockCandidate);
        Assert.Equal(1, accepted.Difficulty);
        var replacement = new ProgpowWorkerJob("2", context.ExtraNonce1);
        ((ProgpowJob) source.ForWorker(context, preserveId: true)).PrepareWorkerJob(replacement, out header);
        Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() =>
            replacement.ProcessShare(LogManager.GetCurrentClassLogger(), worker, "ABCDEF0100000001", header, Cache.Mix)).Code);
    }
}
