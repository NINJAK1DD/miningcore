using System.Globalization;
using Miningcore.Stratum;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowWorkerJob
{
    public ProgpowWorkerJob(string jobId, string extraNonce1)
    {
        Id = jobId;
        ExtraNonce1 = extraNonce1;
    }

    public string Id { get; }
    public ProgpowJob Job { get; set; }
    public uint Height { get; set; }
    public string ExtraNonce1 { get; set; }
    public string Bits { get; set; }
    public string SeedHash { get; set; }

    public (Share Share, string BlockHex) ProcessShare(ILogger logger, StratumConnection worker, string nonce, string headerHash, string mixHash)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<ProgpowWorkerContext>();

        // mixHash
        if(mixHash.Length != 64)
            throw new StratumException(StratumError.Other, $"incorrect size of mixHash: {mixHash}");

        // validate nonce
        if(nonce.Length != 16)
            throw new StratumException(StratumError.Other, $"incorrect size of nonce: {nonce}");

        // check if nonce is within range
        if(nonce.IndexOf(context.ExtraNonce1[0..4], StringComparison.OrdinalIgnoreCase) != 0)
            throw new StratumException(StratumError.Other, $"nonce out of range: {nonce}");

        if(Job.IsDuplicateWorkerProof(context.ExtraNonce1, nonce, headerHash, mixHash))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");
        var nonceLong = ulong.Parse(nonce, NumberStyles.HexNumber);

        var result = Job.ProcessShareInternal(logger, worker, nonceLong, headerHash, mixHash);
        if(!Job.RegisterWorkerProof(context.ExtraNonce1, nonce, headerHash, mixHash))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");
        return result;
    }
}
