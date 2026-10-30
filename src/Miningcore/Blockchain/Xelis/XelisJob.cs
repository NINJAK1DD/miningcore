using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Miningcore.Blockchain.Xelis.DaemonResponses;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;

namespace Miningcore.Blockchain.Xelis;

public class XelisJob : Miningcore.Mining.ICandidateCreditJob
{
    private XelisWorkEpoch epoch = new();
    internal bool IsRetired => epoch.IsRetired;
    internal void BindEpoch(XelisWorkEpoch value) => epoch = value;
    internal void EnsureActive() => epoch.EnsureActive();
    double Miningcore.Mining.ICandidateCreditJob.CandidateDifficulty =>
        (double) new BigRational(XelisConstants.Diff1Target, new System.Numerics.BigInteger(blockTargetValue.ToBytes(), isUnsigned: true)) * shareMultiplier;

    internal XelisJob ForWorker(Miningcore.Mining.WorkerContextBase context, Func<string> nextJobId = null)
    {
        EnsureActive();
        return context.JobForDifficulty(this, (_, id) =>
        {
            var result = (XelisJob) MemberwiseClone();
            result.JobId = id;
            if(result.jobParams != null)
            {
                result.jobParams = (object[]) result.jobParams.Clone();
                result.jobParams[0] = id;
            }
            return result;
        }, JobId, nextJobId);
    }

    protected IMasterClock clock;
    protected double shareMultiplier;
    protected readonly IHashAlgorithm blake3Hasher = new Blake3IHash();
    protected readonly IHashAlgorithm xelisHash = new XelisHash();
    protected readonly IHashAlgorithm xelisHashV2 = new XelisHashV2();

    protected readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);
    public byte[] blockTargetBytes { get; protected set; }
    public uint256 blockTargetValue { get; protected set; }
    protected byte[] prevHashBytes;
    protected byte[] versionBytes;
    protected byte[] heightBytes;
    protected byte[] nTimeBytes;
    protected byte[] extraNonceBytes;
    protected byte[] tipsCountBytes;
    protected byte[] tipsBytes;
    protected byte[] transactionsCountBytes;
    protected byte[] transactionsBytes;
    protected byte[] minerPublicKeyBytes;

    protected object[] jobParams;

    #region API-Surface

    public GetBlockHeaderResponse BlockHeader { get; protected set; }
    public GetBlockTemplateResponse BlockTemplate { get; protected set; }
    public string PrevHash { get; protected set; }
    public double Difficulty { get; protected set; }
    public BigInteger ExactDifficulty { get; protected set; }
    public string Algorithm { get; protected set; }
    public string network { get; protected set; }

    public string JobId { get; protected set; }

    protected virtual byte[] SerializeCoinbase(byte[] extranonceBytes, byte[] nonceBytes)
    {
        using(var stream = new MemoryStream(XelisConstants.BlockWorkSize))
        {
            var bw = new BinaryWriter(stream);

            bw.Write(prevHashBytes);
            bw.Write(nTimeBytes);
            bw.Write(nonceBytes);
            bw.Write(extranonceBytes);
            bw.Write(minerPublicKeyBytes);
            
            return stream.ToArray();
        }
    }

    protected virtual byte[] SerializeHeader(byte[] extranonceBytes, byte[] nonceBytes)
    {
        using(var stream = new MemoryStream())
        {
            var bw = new BinaryWriter(stream);

            bw.Write(versionBytes);
            bw.Write(heightBytes);
            bw.Write(nTimeBytes);
            bw.Write(nonceBytes);
            bw.Write(extranonceBytes);
            bw.Write(tipsCountBytes);

            if(tipsBytes != null)
                bw.Write(tipsBytes);

            bw.Write(transactionsCountBytes);

            if(transactionsBytes != null)
                bw.Write(transactionsBytes);

            bw.Write(minerPublicKeyBytes);

            return stream.ToArray();
        }
    }

    protected virtual (Share Share, string BlockTemplate) ProcessShareInternal(
        StratumConnection worker, string nonce)
    {
        var context = worker.ContextAs<XelisWorkerContext>();
        var extranonceBytes = context.ExtraNonce1.HexToByteArray();
        var nonceBytes = nonce.HexToByteArray();

        var coinbaseBytes = SerializeCoinbase(extranonceBytes, nonceBytes);
        Span<byte> hashCoinbaseBytes = stackalloc byte[XelisConstants.HashSize];

        if(Algorithm == XelisConstants.AlgorithmXelisHashV2)
            xelisHashV2.Digest(coinbaseBytes, hashCoinbaseBytes);
        else if(Algorithm == XelisConstants.AlgorithmXelisHashV1)
            xelisHash.Digest(coinbaseBytes, hashCoinbaseBytes);
        else
            throw new InvalidDataException("Unsupported Xelis proof algorithm");

        var hashCoinbaseInteger = new BigInteger(hashCoinbaseBytes, true, true);
        var hashCoinbaseBytesValue = new uint256(hashCoinbaseBytes.ToNewReverseArray());

        // calc share-diff
        // Zero is a valid proof below every positive target. Its unbounded proof
        // difficulty is used only for admission; stored credit remains finite,
        // bounded by the issued assignment and the job's candidate target.
        var shareDiff = hashCoinbaseInteger.IsZero ? double.PositiveInfinity :
            (double) new BigRational(XelisConstants.Diff1Target, hashCoinbaseInteger) * shareMultiplier;

        // diff check

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = hashCoinbaseBytesValue <= blockTargetValue;

        var stratumDifficulty = context.ValidateShareDifficulty(shareDiff, isBlockCandidate, this);

        var result = new Share
        {
            BlockHeight = (long) BlockHeader.TopoHeight,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty,
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;

            Span<byte> blockHashBytes = stackalloc byte[XelisConstants.HashSize];
            blake3Hasher.Digest(coinbaseBytes, blockHashBytes);

            result.BlockHash = blockHashBytes.ToHexString();

            var blockHeaderBytes = SerializeHeader(extranonceBytes, nonceBytes);
            // Each result owns its solved header. Worker snapshots share only immutable job input.
            return (result, blockHeaderBytes.ToHexString());
        }

        return (result, null);
    }

    public virtual void Init(GetBlockHeaderResponse blockHeader, GetBlockTemplateResponse blockTemplate, string jobId, IMasterClock clock, string network, double shareMultiplier, string prevHash)
    {
        Contract.RequiresNonNull(blockHeader);
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(network);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));
        Contract.RequiresNonNull(shareMultiplier);

        if(!XelisUtils.HasCompatibleConsensus(blockHeader, blockTemplate))
            throw new InvalidDataException("Incompatible Xelis consensus parameters");
        Algorithm = blockTemplate.Algorithm;
        ExactDifficulty = blockTemplate.Difficulty;

        this.clock = clock;
        BlockHeader = blockHeader;
        BlockTemplate = blockTemplate;
        JobId = jobId;
        if(shareMultiplier != 1d)
            throw new ArgumentOutOfRangeException(nameof(shareMultiplier), "Xelis requires shareMultiplier=1");
        this.shareMultiplier = shareMultiplier;

        this.network = network;

        Difficulty = (double) BlockTemplate.Difficulty;
        blockTargetBytes = XelisUtils.DifficultyToTarget(BlockTemplate.Difficulty);
        blockTargetValue = new uint256(blockTargetBytes.ToNewReverseArray());

        PrevHash = prevHash;
        prevHashBytes = PrevHash.HexToByteArray();

        var blockHeaderBytes = BlockHeader.Template.HexToByteArray();
        var blockTemplateBytes = BlockTemplate.Template.HexToByteArray();

        versionBytes = blockHeaderBytes.Take(XelisConstants.BlockHeaderOffsetHeight - XelisConstants.BlockHeaderOffsetVersion).ToArray();
        heightBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetHeight).Take(XelisConstants.BlockHeaderOffsetTimestamp - XelisConstants.BlockHeaderOffsetHeight).ToArray();

        //nTimeBytes = (BitConverter.IsLittleEndian ? BitConverter.GetBytes(XelisUtils.UnixTimeStamp(clock.Now)).ReverseInPlace() : BitConverter.GetBytes(XelisUtils.UnixTimeStamp(clock.Now))); // xelis_daemon expects a big endian format.
        nTimeBytes = blockTemplateBytes.Skip(XelisConstants.BlockTemplateOffsetTimestamp).Take(XelisConstants.BlockTemplateOffsetNonce - XelisConstants.BlockTemplateOffsetTimestamp).ToArray();

        extraNonceBytes = blockTemplateBytes.Skip(XelisConstants.BlockTemplateOffsetExtraNonce).Take(XelisConstants.BlockTemplateOffsetMinerPublicKey - XelisConstants.BlockTemplateOffsetExtraNonce).ToArray();

        tipsCountBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTipsCount).Take(XelisConstants.BlockHeaderOffsetTips - XelisConstants.BlockHeaderOffsetTipsCount).ToArray();
        var tipsCount = uint.Parse(tipsCountBytes.ToHexString(), NumberStyles.HexNumber);
        if(tipsCount > 0)
            tipsBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTips).Take(XelisConstants.HashSize * (int) tipsCount).ToArray();

        transactionsCountBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTips + (XelisConstants.HashSize * (int) tipsCount)).Take(XelisConstants.BlockHeaderSizeTransactionsCount).ToArray();
        var transactionsCount = uint.Parse(transactionsCountBytes.ToHexString(), NumberStyles.HexNumber);
        if(transactionsCount > 0)
            transactionsBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTips + (XelisConstants.HashSize * (int) tipsCount) + XelisConstants.BlockHeaderSizeTransactionsCount).Take(XelisConstants.HashSize * (int) transactionsCount).ToArray();

        minerPublicKeyBytes = blockTemplateBytes.Skip(XelisConstants.BlockTemplateOffsetMinerPublicKey).Take(blockTemplateBytes.Length - XelisConstants.BlockTemplateOffsetMinerPublicKey).ToArray();

        jobParams = new object[]
        {
            JobId,
            nTimeBytes.ToHexString(),
            PrevHash,
            Algorithm,
            false
        };
        Miningcore.Mining.CandidateShareCredit.Initialize(this);
    }

    public virtual object GetJobParams(bool isNew)
    {
        EnsureActive();
        var result = (object[]) jobParams.Clone();
        result[^1] = isNew;
        return result;
    }

    protected virtual bool RegisterSubmit(string extraNonce1, string nonce)
    {
        var key = new StringBuilder()
            .Append(extraNonce1)
            .Append(nonce)
            .ToString();

        return submissions.TryAdd(key, true);
    }

    protected virtual bool IsDuplicateSubmit(string extraNonce1, string nonce)
    {
        var key = new StringBuilder()
            .Append(extraNonce1)
            .Append(nonce)
            .ToString();

        return submissions.ContainsKey(key);
    }

    public virtual (Share Share, string BlockTemplate) ProcessShare(StratumConnection worker,
        string nonce)
    {
        EnsureActive();
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<XelisWorkerContext>();

        // validate nonce
        ProofHex.Require(nonce, XelisConstants.NonceLength, "nonce");
        if(nonce.Length != XelisConstants.NonceLength)
            throw new StratumException(StratumError.Other, "incorrect size of nonce");

        // dupe check
        if(IsDuplicateSubmit(context.ExtraNonce1, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");
        var result = ProcessShareInternal(worker, nonce);
        EnsureActive();
        if(!RegisterSubmit(context.ExtraNonce1, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate");

        return result;
    }

    #endregion // API-Surface
}
