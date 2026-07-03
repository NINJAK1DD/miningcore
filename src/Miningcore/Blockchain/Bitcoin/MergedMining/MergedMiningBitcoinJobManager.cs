using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningBitcoinJobManager : BitcoinJobManager
{
    public MergedMiningBitcoinJobManager(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private const string CreateAuxBlock = "createauxblock";
    private const string SubmitAuxBlock = "submitauxblock";

    private MergedMiningConfig mergedMiningConfig;
    private PoolConfig auxiliaryPoolConfig;
    private BitcoinTemplate parentCoin;
    private BitcoinTemplate auxiliaryCoin;
    private RpcClient auxiliaryRpc;

    private bool MergedMiningEnabled => mergedMiningConfig?.Enabled == true;

    protected override bool PollJobsWithBlockTemplateStream => MergedMiningEnabled;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        parentCoin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);

        mergedMiningConfig = pc.Extra.SafeExtensionDataAs<MergedMiningPoolConfigExtra>()?.MergedMining;
        if(!MergedMiningEnabled)
            return;

        if(!string.Equals(parentCoin.Symbol, "LTC", StringComparison.OrdinalIgnoreCase))
            throw new PoolStartupException("Merged mining currently requires Litecoin as the parent chain", pc.Id);

        if(pc.PaymentProcessing?.Enabled != true || pc.PaymentProcessing.PayoutScheme != PayoutScheme.SOLO)
            throw new PoolStartupException("Merged mining requires enabled SOLO payment processing on the parent pool", pc.Id);

        if(string.IsNullOrWhiteSpace(mergedMiningConfig.AuxPoolId))
            throw new PoolStartupException("mergedMining.auxPoolId is required", pc.Id);

        auxiliaryPoolConfig = cc.Pools.FirstOrDefault(x =>
            string.Equals(x.Id, mergedMiningConfig.AuxPoolId, StringComparison.OrdinalIgnoreCase));

        if(auxiliaryPoolConfig == null)
            throw new PoolStartupException($"Auxiliary pool '{mergedMiningConfig.AuxPoolId}' was not found", pc.Id);

        if(string.Equals(auxiliaryPoolConfig.Id, pc.Id, StringComparison.OrdinalIgnoreCase))
            throw new PoolStartupException("Parent and auxiliary pool ids must be different", pc.Id);

        if(auxiliaryPoolConfig.Enabled != true)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' must be enabled", pc.Id);

        auxiliaryCoin = auxiliaryPoolConfig.Template as BitcoinTemplate;
        if(auxiliaryCoin == null)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' must use the Bitcoin coin family", pc.Id);

        if(!string.Equals(auxiliaryCoin.Symbol, "DOGE", StringComparison.OrdinalIgnoreCase))
            throw new PoolStartupException("Merged mining currently requires Dogecoin as the auxiliary chain", pc.Id);

        if(auxiliaryPoolConfig.PaymentProcessing?.Enabled != true ||
            auxiliaryPoolConfig.PaymentProcessing.PayoutScheme != PayoutScheme.SOLO)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' must use enabled SOLO payment processing", pc.Id);

        if(string.IsNullOrWhiteSpace(auxiliaryPoolConfig.Address))
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' requires a pool wallet address", pc.Id);

        if(auxiliaryPoolConfig.Daemons == null || auxiliaryPoolConfig.Daemons.Length == 0)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' requires a daemon endpoint", pc.Id);

        if(string.IsNullOrWhiteSpace(mergedMiningConfig.AddressParameter))
            mergedMiningConfig.AddressParameter = "doge";
        else
            mergedMiningConfig.AddressParameter = mergedMiningConfig.AddressParameter.Trim();

        if(!MergedMiningPasswordParser.IsValidAddressParameter(mergedMiningConfig.AddressParameter))
            throw new PoolStartupException(
                "mergedMining.addressParameter must not be 'd' or contain ';' or '='",
                pc.Id);

        // Parent ZMQ and Bitcoin Template Stream notifications do not report auxiliary-chain
        // tip changes. Polling guarantees that a fresh Dogecoin template is broadcast independently.
        if(pc.BlockRefreshInterval <= 0)
            pc.BlockRefreshInterval = 1000;

        var serializerSettings = ctx.Resolve<JsonSerializerSettings>();
        auxiliaryRpc = new RpcClient(auxiliaryPoolConfig.Daemons.First(), serializerSettings,
            messageBus, auxiliaryPoolConfig.Id);
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        await base.EnsureDaemonsSynchedAsync(ct);

        if(!MergedMiningEnabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var notificationShown = false;

        do
        {
            var response = await GetAuxBlockTemplateAsync(ct);
            if(response.Error == null && response.Response != null)
            {
                logger.Info(() => $"Auxiliary daemon for {auxiliaryCoin.Name} is synched");
                return;
            }

            if(!notificationShown)
            {
                logger.Info(() => $"Auxiliary daemon for {auxiliaryCoin.Name} is still syncing");
                notificationShown = true;
            }

            if(response.Error != null)
                logger.Debug(() => $"Auxiliary daemon reports: {response.Error.Message}");
        } while(await timer.WaitForNextTickAsync(ct));
    }

    private Task<RpcResponse<AuxBlockTemplate>> GetAuxBlockTemplateAsync(CancellationToken ct)
    {
        return auxiliaryRpc.ExecuteAsync<AuxBlockTemplate>(logger, CreateAuxBlock, ct,
            new[] { auxiliaryPoolConfig.Address });
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct,
        bool forceUpdate, string via = null, string json = null)
    {
        if(!MergedMiningEnabled)
            return await base.UpdateJob(ct, forceUpdate, via, json);

        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var parentResponse = string.IsNullOrEmpty(json)
                ? await GetBlockTemplateAsync(ct)
                : GetBlockTemplateFromJson(json);

            if(parentResponse.Error != null || parentResponse.Response == null)
            {
                var error = parentResponse.Error?.Message ?? "empty response";
                logger.Warn(() => $"Unable to update parent job: {error}");
                return (false, forceUpdate);
            }

            var auxiliaryResponse = await GetAuxBlockTemplateAsync(ct);
            if(auxiliaryResponse.Error != null || auxiliaryResponse.Response == null)
            {
                var error = auxiliaryResponse.Error?.Message ?? "empty response";
                logger.Warn(() => $"Unable to update auxiliary job: {error}");
                return (false, forceUpdate);
            }

            var blockTemplate = parentResponse.Response;
            var auxiliaryTemplate = auxiliaryResponse.Response;
            var previousJob = currentJob as MergedMiningBitcoinJob;
            var previousHeight = previousJob?.BlockTemplate?.Height ?? 0;

            var parentIsNew = previousJob == null ||
                previousJob.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                blockTemplate.Height > previousHeight;

            var auxiliaryIsNew = previousJob == null ||
                !string.Equals(previousJob.AuxiliaryBlockTemplate?.Hash, auxiliaryTemplate.Hash,
                    StringComparison.OrdinalIgnoreCase);

            if(parentIsNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(auxiliaryIsNew)
                messageBus.NotifyChainHeight(auxiliaryPoolConfig.Id, auxiliaryTemplate.Height,
                    auxiliaryPoolConfig.Template);

            if(parentIsNew || auxiliaryIsNew || forceUpdate)
            {
                var job = new MergedMiningBitcoinJob();
                job.InitMerged(blockTemplate, auxiliaryTemplate, NextJobId(), poolConfig,
                    extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, parentCoin.CoinbaseHasherValue, parentCoin.HeaderHasherValue,
                    !isPoS ? parentCoin.BlockHasherValue : parentCoin.PoSBlockHasherValue ?? parentCoin.BlockHasherValue);

                if(parentIsNew)
                {
                    logger.Info(() => via != null
                        ? $"Detected new parent block {blockTemplate.Height} [{via}]"
                        : $"Detected new parent block {blockTemplate.Height}");

                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }
                else if(auxiliaryIsNew)
                {
                    logger.Info(() => $"Detected new auxiliary block {auxiliaryTemplate.Height} [{auxiliaryTemplate.Hash}]");
                }
                else
                {
                    logger.Debug(() => via != null
                        ? $"Merged template update {blockTemplate.Height} [{via}]"
                        : $"Merged template update {blockTemplate.Height}");
                }

                currentJob = job;
            }

            return (parentIsNew || auxiliaryIsNew, forceUpdate);
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    public async Task<bool> ValidateAuxiliaryAddressAsync(string address, CancellationToken ct)
    {
        if(!MergedMiningEnabled)
            return true;

        if(string.IsNullOrWhiteSpace(address))
            return !mergedMiningConfig.RequireAuxAddress;

        var result = await auxiliaryRpc.ExecuteAsync<ValidateAddressResponse>(logger,
            BitcoinCommands.ValidateAddress, ct, new[] { address });

        return result.Error == null && result.Response is { IsValid: true };
    }

    public override async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        if(!MergedMiningEnabled)
            return await base.SubmitShareAsync(worker, submission, ct);

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
            logger.Info(() => $"Submitting parent block {share.BlockHeight} [{share.BlockHash}]");
            var acceptResponse = await SubmitBlockAsync(share, result.ParentBlockHex, ct);
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
                OnBlockFound();
                logger.Info(() => $"Parent daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");
            }
            else
                share.TransactionConfirmationData = null;
        }

        if(!string.IsNullOrEmpty(result.AuxPowHex))
            await SubmitAuxiliaryBlockAsync(worker, context, result, ct);

        return share;
    }

    private async Task SubmitAuxiliaryBlockAsync(StratumConnection worker,
        MergedMiningBitcoinWorkerContext context, MergedMiningShareResult result, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(context.AuxiliaryMiner))
        {
            logger.Warn(() => $"Skipping auxiliary block {result.AuxiliaryBlockTemplate.Height}: worker supplied no auxiliary address");
            return;
        }

        var template = result.AuxiliaryBlockTemplate;
        logger.Info(() => $"Submitting auxiliary block {template.Height} [{template.Hash}]");

        var submitResponse = await auxiliaryRpc.ExecuteAsync<JToken>(logger, SubmitAuxBlock, ct,
            new object[] { template.Hash, result.AuxPowHex });

        var accepted = submitResponse.Error == null &&
            submitResponse.Response?.Type == JTokenType.Boolean &&
            submitResponse.Response.Value<bool>();

        if(!accepted)
        {
            var error = submitResponse.Error?.Message ?? submitResponse.Response?.ToString() ?? "rejected";
            logger.Warn(() => $"Auxiliary block {template.Height} [{template.Hash}] was not accepted: {error}");
            return;
        }

        OnBlockFound();

        var coinbaseTransaction = await GetAuxiliaryCoinbaseTransactionAsync(template.Hash, ct);
        if(string.IsNullOrEmpty(coinbaseTransaction))
        {
            var message = $"Auxiliary daemon accepted block {template.Height} [{template.Hash}], but its coinbase transaction could not be retrieved";
            logger.Error(() => message);
            messageBus.SendMessage(new AdminNotification("Auxiliary block accounting failed", message));
            return;
        }

        var auxiliaryShare = new Share
        {
            PoolId = auxiliaryPoolConfig.Id,
            Miner = context.AuxiliaryMiner,
            Worker = context.Worker,
            UserAgent = context.UserAgent,
            IpAddress = worker.RemoteEndpoint?.Address?.ToString(),
            Source = clusterConfig.ClusterName,
            Difficulty = result.Share.Difficulty,
            ShareDifficulty = result.Share.ShareDifficulty,
            ActualDifficulty = result.Share.ActualDifficulty,
            NetworkDifficulty = result.AuxiliaryDifficulty,
            BlockHeight = template.Height,
            BlockHash = template.Hash,
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = coinbaseTransaction,
            SessionId = context.SessionId,
            Created = clock.Now,
        };

        messageBus.SendMessage(auxiliaryShare);
        logger.Info(() => $"Auxiliary daemon accepted block {template.Height} [{template.Hash}] submitted by {context.AuxiliaryMiner}");
    }

    private async Task<string> GetAuxiliaryCoinbaseTransactionAsync(string blockHash, CancellationToken ct)
    {
        for(var attempt = 1; attempt <= 8; attempt++)
        {
            var response = await auxiliaryRpc.ExecuteAsync<DaemonBlock>(logger,
                BitcoinCommands.GetBlock, ct, new object[] { blockHash });

            var coinbase = response.Error == null
                ? response.Response?.Transactions?.FirstOrDefault()
                : null;

            if(!string.IsNullOrEmpty(coinbase))
                return coinbase;

            if(attempt < 8)
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 100), ct);
        }

        return null;
    }
}
