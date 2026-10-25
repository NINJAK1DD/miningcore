using System.Globalization;
using System.Numerics;
using Miningcore.Crypto.Hashing.Ethash;
using Miningcore.Extensions;
using Miningcore.Stratum;
using NBitcoin;
using NLog;

namespace Miningcore.Blockchain.Ethereum;

public class EthereumJob : Miningcore.Mining.ICandidateCreditJob
{
    double Miningcore.Mining.ICandidateCreditJob.CandidateDifficulty => (double) new Miningcore.Util.BigRational(EthereumConstants.BigMaxValue, new System.Numerics.BigInteger(blockTarget.ToBytes(), isUnsigned: true)) / EthereumConstants.Pow2x32;

    internal EthereumJob ForWorker(Miningcore.Mining.WorkerContextBase context, Func<string> nextJobId = null) =>
        context.JobForDifficulty(this, (_, id) =>
        {
            var result = (EthereumJob) MemberwiseClone();
            result.Id = id;
            return result;
        }, Id, nextJobId);

    public EthereumJob(string id, EthereumBlockTemplate blockTemplate, ILogger logger, IEthashLight ethash)
    {
        Id = id;
        BlockTemplate = blockTemplate;
        this.logger = logger;
        this.ethash = ethash;

        var target = blockTemplate.Target;
        if(target.StartsWith("0x"))
            target = target.Substring(2);

        blockTarget = new uint256(target.HexToReverseByteArray());
        Miningcore.Mining.CandidateShareCredit.Initialize(this);
    }

    // A nonce identifies work for this template, independent of its spelling or
    // submitting connection. Snapshots share the same accepted-proof registry.
    protected HashSet<ulong> workerNonces = new();

    public string Id { get; private set; }
    public EthereumBlockTemplate BlockTemplate { get; }
    protected uint256 blockTarget;
    protected ILogger logger;
    protected IEthashLight ethash;

    public record SubmitResult(Share Share, string FullNonceHex = null, string HeaderHash = null, string MixHash = null);

    protected void RegisterNonce(StratumConnection worker, string nonce)
    {
        if(!workerNonces.Add(ulong.Parse(nonce, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
            throw new StratumException(StratumError.MinusOne, "duplicate share");
    }

    public virtual async Task<SubmitResult> ProcessShareAsync(StratumConnection worker,
        string workerName, string fullNonceHex, string solution, CancellationToken ct)
    {
        var context = worker.ContextAs<EthereumWorkerContext>();

        if(!ProofHex.IsHex(fullNonceHex) || fullNonceHex.Length > 16 ||
            !ulong.TryParse(fullNonceHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fullNonce))
            throw new StratumException(StratumError.MinusOne, "bad nonce " + fullNonceHex);

        fullNonceHex = fullNonce.ToString("x16", CultureInfo.InvariantCulture);
        lock(workerNonces)
            if(workerNonces.Contains(fullNonce))
                throw new StratumException(StratumError.MinusOne, "duplicate share");

        // get dag/light cache for block
        var cache = await ethash.GetCacheAsync(logger, BlockTemplate.Height, ct);

        // compute
        if(!cache.Compute(logger, BlockTemplate.Header.HexToByteArray(), fullNonce, out var mixDigest, out var resultBytes))
            throw new StratumException(StratumError.MinusOne, "bad hash");

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
        lock(workerNonces)
            RegisterNonce(worker, fullNonceHex);

        if(share.IsBlockCandidate)
        {
            fullNonceHex = "0x" + fullNonceHex;
            var headerHash = BlockTemplate.Header;
            var mixHash = mixDigest.ToHexString(true);

            share.TransactionConfirmationData = "";

            return new SubmitResult(share, fullNonceHex, headerHash, mixHash);
        }

        return new SubmitResult(share);
    }

    public virtual object[] GetJobParamsForStratum()
    {
        return new object[]
        {
            Id,
            BlockTemplate.Seed.StripHexPrefix(),
            BlockTemplate.Header.StripHexPrefix(),
            true
        };
    }

    public virtual object[] GetWorkParamsForStratum(EthereumWorkerContext context)
    {
        // https://github.com/edsonayllon/Stratum-Implementation-For-Pantheon
        var workerTarget = BigInteger.Divide(EthereumConstants.BigMaxValue, new BigInteger(context.Difficulty * EthereumConstants.Pow2x32));
        var workerTargetString = workerTarget.ToByteArray(false, true).ToHexString(true);

        return new object[]
        {
            BlockTemplate.Header,
            BlockTemplate.Seed,
            workerTargetString,
        };
    }
}
