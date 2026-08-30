using System.Globalization;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningShareResult
{
    public Share Share { get; init; }
    public string ParentBlockHex { get; init; }
    public string ParentHeaderHex { get; init; }
    public string AuxPowHex { get; init; }
    public AuxBlockTemplate AuxiliaryBlockTemplate { get; init; }
    public double AuxiliaryDifficulty { get; init; }
}

public class MergedMiningBitcoinJob : BitcoinJob
{
    private uint256 auxiliaryTargetValue;
    private BitcoinTemplate.BitcoinNetworkParams mergedNetworkParams;

    public AuxBlockTemplate AuxiliaryBlockTemplate { get; protected set; }
    public double AuxiliaryDifficulty { get; private set; }

    public void InitMerged(BlockTemplate blockTemplate, AuxBlockTemplate auxiliaryBlockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig, ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network, bool isPoS, double shareMultiplier,
        IHashAlgorithm coinbaseHasher, IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
    {
        ArgumentNullException.ThrowIfNull(auxiliaryBlockTemplate);

        AuxiliaryBlockTemplate = auxiliaryBlockTemplate;

        var auxiliaryTarget = new Target(auxiliaryBlockTemplate.Bits.HexToByteArray());
        auxiliaryTargetValue = auxiliaryTarget.ToUInt256();
        AuxiliaryDifficulty = auxiliaryTarget.Difficulty;

        var parentCoin = pc.Template.As<BitcoinTemplate>();
        mergedNetworkParams = parentCoin.GetNetwork(network.ChainName);

        base.Init(blockTemplate, jobId, pc, extraPoolConfig, cc, clock, poolAddressDestination, network,
            isPoS, shareMultiplier, coinbaseHasher, headerHasher, blockHasher);
    }

    protected override Script GenerateScriptSigInitial()
    {
        var initial = base.GenerateScriptSigInitial().ToBytes();
        var commitment = AuxPowBuilder.BuildCoinbaseCommitment(AuxiliaryBlockTemplate.Hash);
        var result = initial.Concat(commitment).ToArray();

        var totalScriptLength = result.Length + extraNoncePlaceHolderLength + scriptSigFinalBytes.Length;
        if(totalScriptLength > 100)
            throw new InvalidOperationException($"Merged-mining coinbase script is {totalScriptLength} bytes; maximum is 100 bytes");

        return new Script(result);
    }

    public MergedMiningShareResult ProcessShareMerged(StratumConnection worker, string extraNonce2,
        string nTime, string nonce, string versionBits = null)
    {
        ArgumentNullException.ThrowIfNull(worker);

        if(string.IsNullOrEmpty(extraNonce2))
            throw new StratumException(StratumError.Other, "missing extranonce2");
        if(string.IsNullOrEmpty(nTime))
            throw new StratumException(StratumError.Other, "missing ntime");
        if(string.IsNullOrEmpty(nonce))
            throw new StratumException(StratumError.Other, "missing nonce");

        var context = worker.ContextAs<MergedMiningBitcoinWorkerContext>();

        if(nTime.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of ntime");

        var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
        if(nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
            throw new StratumException(StratumError.Other, "ntime out of range");

        if(nonce.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of nonce");

        var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);
        var versionBitsInt = ParseVersionBits(context.VersionRollingMask, versionBits);

        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce,
               versionBitsInt))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        return ProcessShareMergedInternal(worker, extraNonce2, nTimeInt, nonceInt, versionBitsInt);
    }

    private MergedMiningShareResult ProcessShareMergedInternal(StratumConnection worker, string extraNonce2,
        uint nTime, uint nonce, uint? versionBits)
    {
        var context = worker.ContextAs<MergedMiningBitcoinWorkerContext>();
        var coinbase = SerializeCoinbase(context.ExtraNonce1, extraNonce2);

        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash, (ulong) nTime, BlockTemplate, coin, mergedNetworkParams);

        var headerValue = new uint256(headerHash);
        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, headerHash.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        var isParentBlockCandidate = headerValue <= blockTargetValue;
        var isAuxiliaryBlockCandidate = headerValue <= auxiliaryTargetValue;

        if(!isParentBlockCandidate && !isAuxiliaryBlockCandidate && ratio < 0.99)
        {
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;
                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                stratumDifficulty = context.PreviousDifficulty.Value;
            }
            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var share = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
            ShareDifficulty = shareDiff,
            ActualDifficulty = shareDiff / shareMultiplier,
            IsBlockCandidate = isParentBlockCandidate,
            RewardBasisSatoshis = rewardToPool.Satoshi,
        };

        string parentBlockHex = null;

        if(isParentBlockCandidate)
        {
            Span<byte> blockHash = stackalloc byte[32];
            blockHasher.Digest(headerBytes, blockHash, nTime);
            share.BlockHash = blockHash.ToHexString();
            parentBlockHex = SerializeBlock(headerBytes, coinbase).ToHexString();
        }

        var auxPowHex = isAuxiliaryBlockCandidate
            ? AuxPowBuilder.BuildAuxPow(coinbase, mt.Steps.ToArray(), headerBytes).ToHexString()
            : null;

        return new MergedMiningShareResult
        {
            Share = share,
            ParentBlockHex = parentBlockHex,
            ParentHeaderHex = headerBytes.ToHexString(),
            AuxPowHex = auxPowHex,
            AuxiliaryBlockTemplate = AuxiliaryBlockTemplate,
            AuxiliaryDifficulty = AuxiliaryDifficulty,
        };
    }
}
