using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Security.Cryptography;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using NBitcoin;

namespace Miningcore.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bJob : BitcoinJob
{
    private BitcoinBlake2bHeader.ConsensusFields consensusFields;
    private BitcoinTemplate.BitcoinNetworkParams blake2bNetwork;
    private byte[] fixedCoinbase;
    private byte[] headerCommitment;
    private byte[] hiddenPreviousBlockHash;
    private BigInteger networkTarget;
    private string initialMinerTime;
    private double? assignedDifficulty;

    internal void InitBlake2b(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
    {
        if(pc.Template is not BitcoinBlake2bTemplate template ||
           template.Family != CoinFamily.BitcoinBlake2b)
            throw new InvalidDataException(
                "Bitcoin BLAKE2b jobs require the isolated typed template");

        blake2bNetwork = template.GetNetwork(network.ChainName) ??
            throw new InvalidDataException(
                $"Bitcoin BLAKE2b does not support daemon network '{network.ChainName}'");
        if(blake2bNetwork.Blake2bActivationHeight is not uint activation ||
           blockTemplate.Height < activation)
            throw new InvalidDataException(
                $"Bitcoin BLAKE2b requires an active header-v2 daemon; " +
                $"template height {blockTemplate.Height} precedes activation " +
                $"{blake2bNetwork.Blake2bActivationHeight?.ToString() ?? "<missing>"}");

        if((blockTemplate.Version & BitcoinBlake2bHeader.HeaderV2Flag) == 0)
            throw new InvalidDataException(
                "Bitcoin BLAKE2b daemon returned a header-v1 template");

        var transactionCount = checked((ulong) (blockTemplate.Transactions?.Length ?? 0) + 1);
        if(transactionCount > ushort.MaxValue)
            throw new InvalidDataException(
                "Bitcoin BLAKE2b template transaction count exceeds uint16 consensus range");

        var bits = BitcoinBlake2bHeader.ParseCompactBits(blockTemplate.Bits);
        networkTarget = BitcoinBlake2bHeader.DecodeCompactTarget(bits);
        var displayedTarget = BitcoinBlake2bHeader.ParseDisplayTarget(
            blockTemplate.Target);
        if(displayedTarget != networkTarget)
            throw new InvalidDataException(
                "Bitcoin BLAKE2b daemon target does not match its compact bits");

        // Bitcoin's generic job initializer owns coinbase construction,
        // transaction merkle branches, reward splitting and block payloads.
        // Header hashing and the miner wire contract are replaced below.
        InitWithCoinbasePolicy(blockTemplate, jobId, pc, extraPoolConfig, cc,
            clock, poolAddressDestination, network, false, shareMultiplier,
            coinbaseHasher, headerHasher, blockHasher,
            bip54CoinbaseEnabled: false);
        Difficulty = BitcoinBlake2bHeader.DifficultyForHash(networkTarget);

        var fixedExtraNonce1 = new string('0',
            BitcoinBlake2bHeader.ConnectionExtraNonceSize * 2);
        var fixedExtraNonce2 = new string('0',
            (BitcoinConstants.ExtranoncePlaceHolderLength -
             BitcoinBlake2bHeader.ConnectionExtraNonceSize) * 2);
        fixedCoinbase = SerializeCoinbase(fixedExtraNonce1, fixedExtraNonce2);
        var coinbaseTransaction = NBitcoin.Transaction.Parse(fixedCoinbase.ToHexString(), network);
        if(coinbaseTransaction.Inputs.Count != 1 ||
           coinbaseTransaction.Inputs[0].ScriptSig.Length is < 2 or > 100)
            throw new InvalidDataException("Bitcoin BLAKE2b coinbase scriptSig must be 2 through 100 bytes; shorten coinbaseString");

        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(fixedCoinbase, coinbaseHash);
        var merkleRootInternal = mt.WithFirst(coinbaseHash.ToArray());
        var merkleRootDisplay = new uint256(merkleRootInternal).ToString()
            .HexToByteArray();

        consensusFields = new BitcoinBlake2bHeader.ConsensusFields(
            blockTemplate.Version,
            BitcoinBlake2bHeader.ParseExactHex(
                blockTemplate.PreviousBlockhash, 32, "previousblockhash"),
            merkleRootDisplay,
            blockTemplate.CurTime,
            bits,
            checked((ushort) transactionCount),
            0, // profile 0, fixed consensus time; reference gateway default
            0,
            new byte[16],
            blockTemplate.Height,
            new byte[32]);
        headerCommitment = BitcoinBlake2bHeader.HeaderCommitment(consensusFields);
        hiddenPreviousBlockHash = BitcoinBlake2bHeader
            .HiddenPreviousBlockHash(consensusFields.PreviousBlockHash);

        Span<byte> minerTime = stackalloc byte[8];
        // Profile 0 uses the upper half as nonce3. Seeding it with curtime
        // matches the reviewed gateway while the consensus nTime remains the
        // committed TimeOnWire value.
        BinaryPrimitives.WriteUInt32LittleEndian(minerTime[4..],
            blockTemplate.CurTime);
        initialMinerTime = minerTime.ToHexString();

        jobParams = BuildJobParams(false, Difficulty);
    }

    protected override byte[] BuildScriptSigFinalBytes(string coinbaseString)
    {
        var result = base.BuildScriptSigFinalBytes(coinbaseString);
        // The commitment changes across jobs, processes and restarts even
        // when their four-byte connection counters and daemon templates match.
        // Miners cannot choose or remove this 128-bit job discriminator.
        result = result.Concat(new Script(Op.GetPushOp(
            RandomNumberGenerator.GetBytes(16))).ToBytes()).ToArray();
        if(blake2bNetwork?.Blake2bActivationHeight != BlockTemplate?.Height ||
           string.IsNullOrEmpty(blake2bNetwork.Blake2bActivationHeadline))
            return result;

        var headline = Encoding.ASCII.GetBytes(
            blake2bNetwork.Blake2bActivationHeadline);
        var pushedHeadline = new Script(Op.GetPushOp(headline)).ToBytes();
        return result.Concat(pushedHeadline).ToArray();
    }

    internal BitcoinBlake2bJob ForDifficulty(double difficulty)
    {
        BitcoinBlake2bHeader.TargetForDifficulty(difficulty);
        var result = (BitcoinBlake2bJob) MemberwiseClone();
        result.assignedDifficulty = difficulty;
        result.JobId = JobId + "-" + BitConverter.DoubleToInt64Bits(difficulty).ToString("x16");
        return result;
    }

    public override object GetJobParams(bool isNew) =>
        BuildJobParams(isNew, assignedDifficulty ?? Difficulty);

    private object[] BuildJobParams(bool isNew, double difficulty)
    {
        var shareTarget = BitcoinBlake2bHeader.TargetForDifficulty(difficulty);
        var shareBits = BitcoinBlake2bHeader.EncodeCompactTarget(shareTarget);

        return new object[]
        {
            JobId,
            hiddenPreviousBlockHash?.ToHexString(),
            headerCommitment == null
                ? null
                : BitcoinBlake2bHeader.Coinbase1(headerCommitment)
                    .ToHexString(),
            string.Empty,
            Array.Empty<string>(),
            BlockTemplate?.Version.ToString("x8"),
            shareBits.ToString("x8"),
            initialMinerTime,
            isNew,
        };
    }

    public override (Share Share, string BlockHex) ProcessShare(
        StratumConnection worker, string extraNonce2, string nTime,
        string nonce, string versionBits = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var context = worker.ContextAs<BitcoinWorkerContext>();

        byte[] connectionExtraNonce;
        byte[] minerExtraNonce;
        byte[] minerTime;
        byte[] minerNonce;
        try
        {
            connectionExtraNonce = BitcoinBlake2bHeader.ParseExactHex(
                context.ExtraNonce1,
                BitcoinBlake2bHeader.ConnectionExtraNonceSize,
                "connection extranonce");
            minerExtraNonce = BitcoinBlake2bHeader.ParseExactHex(extraNonce2,
                BitcoinBlake2bHeader.MinerExtraNonceSize,
                "extranonce2");
            minerTime = BitcoinBlake2bHeader.ParseExactHex(nTime,
                BitcoinBlake2bHeader.MinerTimeSize, "ntime");
            minerNonce = BitcoinBlake2bHeader.ParseExactHex(nonce,
                BitcoinBlake2bHeader.MinerNonceSize, "nonce");
        }
        catch(InvalidDataException ex)
        {
            throw new StratumException(StratumError.Other, ex.Message);
        }

        if(versionBits != null)
            throw new StratumException(StratumError.Other,
                "version_bits is not supported by Bitcoin BLAKE2b header-v2");

        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce,
               null))
            throw new StratumException(StratumError.DuplicateShare,
                "duplicate share");

        var combinedExtraNonce = new byte[12];
        connectionExtraNonce.CopyTo(combinedExtraNonce, 0);
        minerExtraNonce.CopyTo(combinedExtraNonce, 4);
        var headerExtraNonce = new byte[16];
        combinedExtraNonce.CopyTo(headerExtraNonce, 4);

        var hash = BitcoinBlake2bHeader.ComputeUnmaskedProfile0Hash(
            consensusFields, headerCommitment, hiddenPreviousBlockHash, minerNonce, minerTime,
            headerExtraNonce);
        var hashValue = BitcoinBlake2bHeader.HashValue(hash);
        var stratumDifficulty = assignedDifficulty ?? throw new InvalidOperationException(
            "Bitcoin BLAKE2b shares require an issued difficulty snapshot");
        var assignedTarget = BitcoinBlake2bHeader.TargetForDifficulty(
            stratumDifficulty);
        var (accepted, isBlockCandidate) = BitcoinBlake2bHeader.ClassifyProof(
            hashValue, assignedTarget, networkTarget);
        if(!accepted)
        {
            var actual = BitcoinBlake2bHeader.DifficultyForHash(hashValue);
            throw new StratumException(StratumError.LowDifficultyShare,
                $"low difficulty share ({actual})");
        }

        var actualDifficulty = BitcoinBlake2bHeader.DifficultyForHash(hashValue);
        var share = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
            ShareDifficulty = actualDifficulty * shareMultiplier,
            ActualDifficulty = actualDifficulty,
            IsBlockCandidate = isBlockCandidate,
        };

        if(!isBlockCandidate)
            return (share, null);

        var header = BitcoinBlake2bHeader.Serialize(consensusFields,
            minerNonce, minerTime, headerExtraNonce);
        share.BlockHash = hash.ToHexString();
        return (share, SerializeBlock(header, fixedCoinbase).ToHexString());
    }
}
