using Autofac;
using Miningcore.Blockchain;
using Miningcore.Contracts;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningSubmitCheck : BitcoinJobManager
{
    public MergedMiningSubmitCheck(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    public override async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams || submitParams.Length < 5)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<MergedMiningBitcoinWorkerContext>();
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;
        var versionBits = context.VersionRollingMask.HasValue && submitParams.Length > 5
            ? submitParams[5] as string
            : null;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        MergedMiningBitcoinJob job;
        lock(context)
        {
            job = context.GetJob(jobId) as MergedMiningBitcoinJob;
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        var result = job.ProcessShareMerged(worker, extraNonce2, nTime, nonce, versionBits);
        var share = result.Share;

        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint?.Address?.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        if(share.IsBlockCandidate)
        {
            var acceptResponse = await SubmitBlockAsync(share, result.ParentBlockHex, ct);
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
                OnBlockFound();
            }
            else
                share.TransactionConfirmationData = null;
        }

        return share;
    }
}
