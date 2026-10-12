using System;
using System.Globalization;
using System.Numerics;
using System.Reactive.Threading.Tasks;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Extensions;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using NLog;

namespace Miningcore.Blockchain.Ethereum.Custom.Cortex;

public class CortexJob : EthereumJob
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Nonce, string Solution), bool> acceptedProofs = new();
    protected CortexCuckooCycle cortexCuckooCycleHasher;
    protected Sha3_256 sha3Hasher;
    protected Blake2b blake2bHasher;

    public CortexJob(string id, EthereumBlockTemplate blockTemplate, ILogger logger, IEthashLight ethash) : base(id, blockTemplate, logger, ethash)
    {
        this.cortexCuckooCycleHasher = new CortexCuckooCycle();
        this.sha3Hasher = new Sha3_256();
        this.blake2bHasher = new Blake2b();
    }

    protected virtual byte[] SerializeHeader(ulong nonce)
    {
        using(var stream = new MemoryStream(CortexConstants.CuckarooHeaderNonceSize))
        {
            var bw = new BinaryWriter(stream);

            bw.Write(BlockTemplate.Header.HexToByteArray());
            bw.Write((BitConverter.IsLittleEndian ? BitConverter.GetBytes(nonce) : BitConverter.GetBytes(nonce).ReverseInPlace())); // cortex-cuckoo-cycles expects a little endian format.

            return stream.ToArray();
        }
    }

    protected virtual uint[] SerializeSolution(string solution)
    {
        // allocate a uint array of size 42
        var solutionUints = new uint[CortexConstants.CuckarooSolutionSize];
        var solutionBytes = (Span<byte>) solution.HexToByteArray();

        // fill the buffer with the big-endian representation of each uint in solution
        for (int i = 0; i < solutionUints.Length; i++)
        {
            var slice = solutionBytes.Slice(i * 4, 4);

            solutionUints[i] = BitConverter.ToUInt32(slice);
        }

        return solutionUints;
    }

    protected virtual int VerifySolution(byte[] header, uint[] solution) => cortexCuckooCycleHasher.Verify(header, solution);

    protected virtual byte[] SerializeCoinbase(uint[] solution)
    {
        // allocate a byte array of size 42 * 4
        var solutionBytes = new byte[solution.Length * 4];

        // fill the buffer with the big-endian representation of each uint in solution
        for (int i = 0; i < solution.Length; i++)
        {
            var uintBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes(solution[i]).ReverseInPlace() : BitConverter.GetBytes(solution[i]); // sha3_256 expects a big endian format.

            uintBytes.CopyTo(solutionBytes, i * 4);
        }

        var coinbaseBytes = new byte[32];
        sha3Hasher.Digest(solutionBytes, coinbaseBytes);

        return coinbaseBytes;
    }

    public override async Task<SubmitResult> ProcessShareAsync(StratumConnection worker,
        string workerName, string fullNonceHex, string solution, CancellationToken ct)
    {
        var context = worker.ContextAs<EthereumWorkerContext>();

        if(!ProofHex.IsHex(fullNonceHex) || fullNonceHex.Length > 16 ||
            !ulong.TryParse(fullNonceHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fullNonce))
            throw new StratumException(StratumError.MinusOne, "bad nonce " + fullNonceHex);

        fullNonceHex = fullNonce.ToString("x16", CultureInfo.InvariantCulture);
        if(!ProofHex.IsExact(solution, CortexConstants.CuckarooSolutionSize * sizeof(uint) * 2))
            throw new StratumException(StratumError.MinusOne, "bad solution");
        var identity = (fullNonce, solution.ToLowerInvariant());
        // One graph can have multiple valid cycles. Only the same nonce AND
        // verified 42-edge solution identify the same Cortex proof.
        if(acceptedProofs.ContainsKey(identity))
            throw new StratumException(StratumError.MinusOne, "duplicate share");

        var solutionBytes = SerializeSolution(solution);

        await Task.Run( () =>
        {
            var headerBytes = SerializeHeader(fullNonce);

            if(VerifySolution(headerBytes, solutionBytes) != 0)
                throw new StratumException(StratumError.MinusOne, "bad hash");
        }, ct);

        var resultBytes = SerializeCoinbase(solutionBytes);

        // test if share meets at least workers current difficulty
        resultBytes.ReverseInPlace();
        var resultValue = new uint256(resultBytes);
        var resultValueBig = resultBytes.AsSpan().ToBigInteger();
        var shareDiff = (double) BigInteger.Divide(EthereumConstants.BigMaxValue, resultValueBig) / EthereumConstants.Pow2x32;
        var isBlockCandidate = resultValue <= blockTarget;

        var stratumDifficulty = context.ValidateShareDifficulty(shareDiff, isBlockCandidate, this);

        var share = new Share
        {
            BlockHeight = (long) BlockTemplate.Height,
            IpAddress = worker.RemoteEndpoint?.Address?.ToString(),
            Miner = context.Miner,
            Worker = workerName,
            UserAgent = context.UserAgent,
            IsBlockCandidate = isBlockCandidate,
            Difficulty = stratumDifficulty * EthereumConstants.Pow2x32
        };

        // Commit only validated proofs, before returning to candidate RPC/accounting.
        if(!acceptedProofs.TryAdd(identity, true))
            throw new StratumException(StratumError.MinusOne, "duplicate share");

        if(share.IsBlockCandidate)
        {
            fullNonceHex = "0x" + fullNonceHex;
            var headerHash = BlockTemplate.Header;
            var mixHash = resultBytes.ToHexString(true);

            share.TransactionConfirmationData = "";

            return new SubmitResult(share, fullNonceHex, headerHash, mixHash);
        }

        return new SubmitResult(share);
    }
}
