using System;
using System.Numerics;
using System.Reflection;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Nexa;
using Miningcore.Blockchain.Handshake;
using Miningcore.Blockchain.Satoshicash;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Equihash.Custom.Veruscoin;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Zano;
using Miningcore.Blockchain.Warthog;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Equihash;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

// Final digest / solver outcomes are controlled, while the production target
// comparison, endianness, multiplier, admission and stored-credit mapping run.
// These tests complement native consensus KATs; they are not consensus vectors.
public class CandidateVerifierBoundaryTests
{
    private sealed class Hash(byte[] bytes) : IHashAlgorithm
    {
        public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
        { result.Clear(); bytes.AsSpan(0, Math.Min(result.Length, bytes.Length)).CopyTo(result); }
    }
    private static uint256 UInt(BigInteger n)
    {
        var bytes = n.ToByteArray(true, false);
        Array.Resize(ref bytes, 32);
        return new uint256(bytes);
    }
    private static byte[] Bytes(BigInteger n, bool big = false) => UInt(n).ToBytes(!big);
    private static void Set(object job, string name, object value) => NonBitcoinCandidateCreditTests.Set(job, name, value);
    private static StratumConnection Connect(WorkerContextBase context, double difficulty)
    {
        context.Init(difficulty, null, new MockMasterClock());
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), new MockMasterClock(), "boundary", false);
        worker.SetContext(context);
        return worker;
    }

    private sealed class Nexa : NexaJob
    {
        internal Share Verify(StratumConnection worker) => ProcessShareInternal(worker, "0000000000000001", "00000001").Share;
    }
    private sealed class Hns : HandshakeJob
    {
        internal Share Verify(StratumConnection worker) => ProcessShareInternal(worker, "00", 1, 1).Share;
        protected override byte[] SerializeExtranonce(string a, string b) => new byte[32];
        protected override byte[] SerializeSubHeader(byte[] extra) => new byte[128];
        protected override byte[] SerializeCommitHash(byte[] sub) => new byte[32];
        protected override byte[] SerializeShare(uint nonce, uint time, byte[] commit) => new byte[128];
        protected override byte[] PaddingPreviousBlockWithTreeRoot(int size) => new byte[size];
        protected override byte[] SerializeHeader(uint nonce, uint time, byte[] extra, byte[] commit) => new byte[236];
        protected override byte[] SerializeCoinbase() => Array.Empty<byte>();
        protected override byte[] SerializeBlock(byte[] header, byte[] coinbase) => header;
    }
    private sealed class Scash(byte[] proof) : SatoshicashJob
    {
        protected override byte[] SerializeCoinbase(string a, string b) => new byte[1];
        protected override byte[] SerializeHeader(Span<byte> coinbase, uint time, uint nonce, byte[] hash) => new byte[112];
        protected override byte[] SerializeBlock(byte[] header, byte[] coinbase) => header;
        protected override void ComputeProof(byte[] header, Span<byte> hash, Span<byte> commitment)
        { hash.Clear(); proof.CopyTo(commitment); }
    }
    private sealed class Xel : XelisJob
    {
        internal Share Verify(StratumConnection worker) => ProcessShareInternal(worker, "0000000000000001");
        protected override byte[] SerializeCoinbase(byte[] extra, byte[] nonce) => new byte[112];
        protected override byte[] SerializeHeader(byte[] extra, byte[] nonce) => new byte[112];
    }
    private sealed class Solver : EquihashSolver
    {
        public override bool Verify(ReadOnlySpan<byte> header, ReadOnlySpan<byte> solution) => true;
    }
    private sealed class Eq(byte[] proof) : EquihashJob
    {
        internal Share Verify(StratumConnection worker) => ProcessShareInternal(worker, "00", 1, "00000000").Share;
        protected override void HashProof(ReadOnlySpan<byte> header, Span<byte> hash, uint time) => proof.CopyTo(hash);
        protected override byte[] SerializeHeader(uint time, string nonce) => new byte[140];
        protected override byte[] SerializeBlock(Span<byte> header, Span<byte> coinbase, Span<byte> solution) => new byte[141];
    }
    private sealed class Verus(byte[] proof) : VeruscoinJob
    {
        internal Share Verify(StratumConnection worker) => ProcessShareInternal(worker, "00", 1, "00000000").Share;
        protected override void HashVerusProof(ReadOnlySpan<byte> header, Span<byte> hash, string version = null) => proof.CopyTo(hash);
        protected override byte[] SerializeHeader(uint time, string nonce) => new byte[140];
        protected override byte[] SerializeBlock(Span<byte> header, Span<byte> coinbase, Span<byte> solution) => new byte[141];
    }

    [Theory]
    [InlineData("Nexa", 1000)] [InlineData("Nexa", .5)]
    [InlineData("Handshake", 1000)] [InlineData("Handshake", .5)]
    [InlineData("Satoshicash", 1000)] [InlineData("Satoshicash", .5)]
    [InlineData("Xelis", 1000)] [InlineData("Xelis", .5)]
    [InlineData("Equihash", 1000)] [InlineData("Equihash", .5)]
    [InlineData("Verus", 1000)] [InlineData("Verus", .5)]
    [InlineData("Nexa", 1000, true)] [InlineData("Handshake", 1000, true)]
    [InlineData("Satoshicash", 1000, true)] [InlineData("Xelis", 1000, true)]
    [InlineData("Equihash", 1000, true)] [InlineData("Verus", 1000, true)]
    public void ProofExactlyAtBlockTargetUsesValidatorUnitsAndStoredMapping(string family, double issuedFactor, bool strong = false)
    {
        const double difficulty = 32;
        var multiplier = family is "Nexa" or "Handshake" or "Satoshicash" ? 4d : 1d;
        var numerator = family == "Xelis" ? XelisConstants.Diff1Target : family is "Equihash" or "Verus" ? EquihashConstants.ZCashDiff1b : BitcoinConstants.Diff1;
        var target = numerator / (BigInteger) difficulty;
        var proofTarget = strong ? target / 2000 : target;
        var raw = family is "Handshake" or "Xelis" ? Bytes(proofTarget, true) : Bytes(proofTarget);
        var hash = new Hash(raw);
        var limit = (double) new Miningcore.Util.BigRational(numerator, target) * multiplier;
        object source;
        WorkerContextBase context;
        Func<StratumConnection, Share> verify;
        if(family == "Nexa")
        {
            var job = new Nexa();
            job.Init(new Miningcore.Blockchain.Nexa.DaemonResponses.MiningCandidate { HeaderCommitment = new string('0', 64) },
                new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate { Height = 101, Target = UInt(target).ToString() },
                "nexa", new PoolConfig { Template = new BitcoinTemplate() }, new MockMasterClock(), multiplier, hash);
            source = job; context = new NexaWorkerContext(); verify = job.Verify;
        }
        else if(family == "Handshake")
        {
            var job = new Hns(); source = job; context = new HandshakeWorkerContext(); verify = job.Verify;
            Set(job, "BlockTemplate", new Miningcore.Blockchain.Handshake.DaemonResponses.HandshakeBlockTemplate { Height = 101 });
            Set(job, "maskBytes", new byte[32]); Set(job, "blockHasher", hash); Set(job, "shareHasher", hash);
        }
        else if(family == "Satoshicash")
        {
            var job = new Scash(raw); source = job; context = new SatoshicashWorkerContext();
            verify = w => job.ProcessShareInternal(w, "00", 1, 1).Share;
            Set(job, "BlockTemplate", new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate { Height = 101 });
            Set(job, "coinbaseHasher", hash); Set(job, "blockHasher", hash);
        }
        else if(family == "Xelis")
        {
            var job = new Xel(); source = job; context = new XelisWorkerContext { ExtraNonce1 = "00" }; verify = job.Verify;
            Set(job, "BlockTemplate", new Miningcore.Blockchain.Xelis.DaemonResponses.GetBlockTemplateResponse());
            Set(job, "BlockHeader", new Miningcore.Blockchain.Xelis.DaemonResponses.GetBlockHeaderResponse { TopoHeight = 101 });
            Set(job, "xelisHash", hash); Set(job, "blake3Hasher", hash);
        }
        else
        {
            source = family == "Verus" ? new Verus(raw) : new Eq(raw);
            context = new EquihashWorkerContext();
            verify = family == "Verus" ? ((Verus) source).Verify : ((Eq) source).Verify;
            Set(source, "BlockTemplate", new Miningcore.Blockchain.Equihash.DaemonResponses.EquihashBlockTemplate { Height = 101, Version = 4 });
            Set(source, "networkParams", new EquihashCoinTemplate.EquihashNetworkParams { Diff1 = numerator.ToString("x") });
            Set(source, "solver", new Solver()); Set(source, "rewardToPool", Money.Zero);
        }
        if(family != "Nexa")
        {
            Set(source, "Difficulty", (double) new Miningcore.Util.BigRational(numerator, target));
            Set(source, "blockTargetValue", UInt(target));
            if(family is "Handshake" or "Satoshicash" or "Xelis") Set(source, "shareMultiplier", multiplier);
            CandidateShareCredit.Initialize(source);
        }
        var worker = Connect(context, limit * issuedFactor);
        context.JobForDifficulty(source, (_, _) => throw new Exception(), "boundary");
        Assert.Equal(limit, CandidateShareCredit.Limit(source), 8);
        var share = verify(worker);
        Assert.True(share.IsBlockCandidate);
        var storedScale = family is "Nexa" or "Handshake" or "Satoshicash" ? multiplier : 1;
        Assert.Equal(Math.Min(limit * issuedFactor, limit) / storedScale, share.Difficulty, 8);
        // A proof immediately outside the block target is ordinary. Its proof
        // strength still qualifies at this lower assignment and keeps full credit.
        Set(source, "blockTargetValue", UInt(proofTarget - 1));
        var ordinary = Connect((WorkerContextBase) Activator.CreateInstance(context.GetType()), limit / 2);
        Assert.False(verify(ordinary).IsBlockCandidate);
        Assert.Equal(limit / 2 / storedScale, verify(ordinary).Difficulty, 8);
    }

    private sealed class Wart : WarthogJob
    {
        internal static byte[] Header => new byte[80];
        internal Share Verify(StratumConnection worker) => ProcessShareInternal(worker, "00", 1, 1).Share;
        protected override byte[] SerializeExtranonce(string a, string b) => new byte[8];
        protected override byte[] SerializeHeader(byte[] extra, uint time, uint nonce) => Header;
    }

    [LinuxNativeTheory]
    [InlineData(false)] [InlineData(true)]
    public void WarthogNativeJanusProofNearEncodedTargetHasMatchingCredit(bool testnet)
    {
        var shaD = new byte[32]; var shaT = new byte[32]; var verus = new byte[32];
        new Sha256D().Digest(Wart.Header, shaD);
        new Sha256S().Digest(shaD, shaT);
        new Miningcore.Native.Verushash().Digest(Wart.Header, verus, VeruscoinConstants.HashVersion2b1);
        var sha = new WarthogCustomFloat(shaT);
        if(sha < WarthogConstants.ProofOfBalancedWorkC) sha = WarthogConstants.ProofOfBalancedWorkC;
        var score = new WarthogCustomFloat(verus) * WarthogCustomFloat.Pow(sha, WarthogConstants.ProofOfBalancedWorkExponent);
        var proofDifficulty = 1 / (double) score;
        var networkDifficulty = proofDifficulty / 1.00001;
        var target = new WarthogTarget(networkDifficulty, true);
        Assert.True(score < target);
        var job = new Wart();
        Set(job, "BlockTemplate", new Miningcore.Blockchain.Warthog.DaemonResponses.WarthogBlockTemplate
            { Data = new Miningcore.Blockchain.Warthog.DaemonResponses.WarthogBlockTemplateData { Height = 1_000_000 } });
        Set(job, "network", testnet ? WarthogNetworkType.Testnet : WarthogNetworkType.Mainnet);
        Set(job, "IsJanusHash", true); Set(job, "Difficulty", networkDifficulty);
        Set(job, "blockTargetValue", target); Set(job, "versionBytes", new byte[] { 0, 0, 0, 2 });
        CandidateShareCredit.Initialize(job);
        var limit = Math.Min(networkDifficulty, target.Difficulty());
        Assert.InRange(proofDifficulty / limit, 1, 1.00002);
        foreach(var factor in new[] { .5, 1000d })
        {
            var context = new WarthogWorkerContext();
            var worker = Connect(context, limit * factor);
            job.ForWorker(context);
            var candidate = job.Verify(worker);
            Assert.True(candidate.IsBlockCandidate);
            Assert.Equal(limit * Math.Min(factor, 1), candidate.Difficulty, 8);
        }
        var ordinaryContext = new WarthogWorkerContext();
        var ordinary = Connect(ordinaryContext, limit / 2);
        Set(job, "blockTargetValue", new WarthogTarget(proofDifficulty * 2, true));
        Assert.False(job.Verify(ordinary).IsBlockCandidate);
        Assert.Equal(limit / 2, job.Verify(ordinary).Difficulty, 8);
    }

    [LinuxNativeTheory]
    [InlineData(1000)] [InlineData(1)]
    public void WarthogLegacySha256CandidateUsesConservativeUnitCredit(double assignment)
    {
        var job = new Wart();
        Set(job, "BlockTemplate", new Miningcore.Blockchain.Warthog.DaemonResponses.WarthogBlockTemplate
            { Data = new Miningcore.Blockchain.Warthog.DaemonResponses.WarthogBlockTemplateData { Height = 1 } });
        Set(job, "network", WarthogNetworkType.Mainnet); Set(job, "IsJanusHash", false);
        Set(job, "Difficulty", 1000d); Set(job, "blockTargetValue", new WarthogTarget(1d, false));
        Set(job, "sha256D", new Hash(Bytes(BigInteger.One, true)));
        Set(job, "versionBytes", new byte[] { 0, 0, 0, 2 });
        CandidateShareCredit.Initialize(job);
        var context = new WarthogWorkerContext();
        var worker = Connect(context, assignment);
        job.ForWorker(context);
        Assert.True(job.Verify(worker).IsBlockCandidate);
        Assert.Equal(1, job.Verify(worker).Difficulty);
    }

    [Fact]
    public void WarthogTestnetRejectsNonJanusConfigurationBeforePublication()
    {
        var job = new WarthogJob();
        Assert.Throws<System.IO.InvalidDataException>(() => job.Init(
            new Miningcore.Blockchain.Warthog.DaemonResponses.WarthogBlockTemplate(), "invalid",
            new MockMasterClock(), WarthogNetworkType.Testnet, false, "prev"));
    }

    private sealed class LinuxNativeTheoryAttribute : TheoryAttribute
    { public LinuxNativeTheoryAttribute() { if(!OperatingSystem.IsLinux()) Skip = "Requires native blob conversion"; } }
    private sealed class CnProof(byte[] bytes)
    {
        public void Conceal(ReadOnlySpan<byte> input, Span<byte> result, ulong height) => bytes.CopyTo(result);
        public void Cryptonote(string realm, string seed, ReadOnlySpan<byte> input, Span<byte> result, ulong height) => bytes.CopyTo(result);
    }
    private sealed class ZanoCache(byte[] bytes) : IProgpowCache
    {
        public byte[] SeedHash { get; set; } = new byte[32];
        public bool Compute(ILogger log, int height, byte[] header, ulong nonce, out byte[] mix, out byte[] result)
        { mix = new byte[32]; result = (byte[]) bytes.Clone(); Array.Reverse(result); return true; }
        public void Dispose() { }
    }
    private sealed class Zano : ZanoJob
    {
        internal Zano(long difficulty, IProgpowCache cache) : base(new Miningcore.Blockchain.Zano.DaemonResponses.GetBlockTemplateResponse
            { Difficulty = difficulty, Height = 101, Blob = "00", SeedHash = new string('0',64) }, new byte[3], new ZanoCoinTemplate { ShareMultiplier = 1 }, new PoolConfig(), new ClusterConfig(), "prev", cache) { }
        protected override byte[] EncodeBlob(uint nonce) => new byte[32];
        protected override byte[] SerializeSubmittedBlock(ulong nonce) => new byte[80];
        protected override void ComputeBlockId(byte[] blob, byte[] hash) => hash.AsSpan().Clear();
    }

    [LinuxNativeTheory]
    [InlineData("Cryptonote", 1)] [InlineData("Cryptonote", 1000)]
    [InlineData("Conceal", 1)] [InlineData("Conceal", 1000)]
    [InlineData("Zano", 1)] [InlineData("Zano", 1000)]
    [InlineData("Cryptonote", 1000, true)] [InlineData("Conceal", 1000, true)] [InlineData("Zano", 1000, true)]
    public void BlobVerifierAtNetworkThresholdCapsHighAssignmentsAndKeepsOrdinaryShares(string family, double issuedFactor, bool strong = false)
    {
        const long difficulty = 32;
        var numerator = family == "Zano" ? ZanoConstants.Diff1b : CryptonoteConstants.Diff1b;
        var raw = Bytes(numerator / (difficulty * (strong ? 2000 : 1)));
        var hash = new CnProof(raw);
        object source;
        WorkerContextBase context;
        Func<StratumConnection, Share> verify;
        if(family == "Zano")
        {
            var job = new Zano(difficulty, new ZanoCache(raw)); source = job; context = new ZanoWorkerContext();
            verify = w => job.ProcessShare(new NullLogger(LogManager.LogFactory), "0000000000000001", 1, w).Share;
        }
        else
        {
            var blob = (string) typeof(PoolFamilyAssignmentTests).GetField("NativeBlob", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            var offset = blob.IndexOf("020800000000012e7f76", StringComparison.Ordinal) / 2 + 2;
            if(family == "Conceal")
            {
                var job = new ConcealJob(new Miningcore.Blockchain.Conceal.DaemonResponses.GetBlockTemplateResponse
                    { Blob = blob, Difficulty = difficulty, Height = 101, ReservedOffset = offset }, new byte[3], "cn", new ConcealCoinTemplate { Hash = CryptonightHashType.CryptonightCCX }, new PoolConfig(), new ClusterConfig(), "prev");
                source = job; context = new ConcealWorkerContext(); verify = w => job.ProcessShare("00000001", 1, raw.ToHexString(), w).Share;
            }
            else
            {
                var job = new CryptonoteJob(new Miningcore.Blockchain.Cryptonote.DaemonResponses.GetBlockTemplateResponse
                    { Blob = blob, Difficulty = difficulty, Height = 101, ReservedOffset = offset }, new byte[3], "cn", new CryptonoteCoinTemplate { Hash = CryptonightHashType.Cryptonight0 }, new PoolConfig(), new ClusterConfig(), "prev", "test");
                source = job; context = new CryptonoteWorkerContext(); verify = w => job.ProcessShare("00000001", 1, raw.ToHexString(), w).Share;
            }
            var field = source.GetType().GetField("hashFunc", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(source, Delegate.CreateDelegate(field.FieldType, hash, family));
        }
        var worker = Connect(context, difficulty * issuedFactor);
        context.RegisterBlobDifficulty(source, 1);
        Assert.Equal(difficulty, CandidateShareCredit.Limit(source));
        var expectedProof = difficulty * (strong ? 2000d : 1d);
        Assert.InRange((double) new Miningcore.Util.BigRational(numerator, new BigInteger(raw, true)), expectedProof, expectedProof * 1.00000001);
        var candidate = verify(worker);
        Assert.True(candidate.IsBlockCandidate);
        Assert.Equal(difficulty, candidate.Difficulty);
        // Independent ordinary work has a harder network threshold and no cap.
        var template = source.GetType().GetProperty("BlockTemplate").GetValue(source);
        template.GetType().GetProperty("Difficulty").SetValue(template, Convert.ChangeType((long) expectedProof * 2, template.GetType().GetProperty("Difficulty").PropertyType));
        var ordinary = Connect((WorkerContextBase) Activator.CreateInstance(context.GetType()), difficulty / 2);
        Assert.False(verify(ordinary).IsBlockCandidate);
        Assert.Equal(difficulty / 2, verify(ordinary).Difficulty);
    }
}
