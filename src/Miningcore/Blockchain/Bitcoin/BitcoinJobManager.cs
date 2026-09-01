using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NBitcoin;
using Org.BouncyCastle.Crypto.Parameters;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJobManager : BitcoinJobManagerBase<BitcoinJob>
{
    public BitcoinJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider,
        IBlockCandidateRecorder blockCandidateRecorder = null) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
        this.blockCandidateRecorder = blockCandidateRecorder;
    }

    private BitcoinTemplate coin;
    private readonly IBlockCandidateRecorder blockCandidateRecorder;
    private BitcoinDirectCoinbaseRecipient[] directCoinbaseRecipients =
        Array.Empty<BitcoinDirectCoinbaseRecipient>();

    protected override object[] GetBlockTemplateParams()
    {
        var result = BuildBlockTemplateParams(coin);

        if(coin.BlockTemplateRpcExtraParams != null)
        {
            if(coin.BlockTemplateRpcExtraParams.Type == JTokenType.Array)
                result = result.Concat(coin.BlockTemplateRpcExtraParams.ToObject<object[]>() ?? Array.Empty<object>()).ToArray();
            else
                result = result.Concat(new []{ coin.BlockTemplateRpcExtraParams.ToObject<object>()}).ToArray();
        }

        return result;
    }

    internal static object[] BuildBlockTemplateParams(BitcoinTemplate coin)
    {
        return new object[]
        {
            new
            {
                rules = coin.HasMWEB ? new[] {"segwit", "mweb"} : new[] {"segwit"},
            }
        };
    }
    
    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var response = await rpc.ExecuteAsync<BlockTemplate>(logger,
                BitcoinCommands.GetBlockTemplate, ct, GetBlockTemplateParams());

            var isSynched = response.Error == null;

            if(isSynched)
            {
                logger.Info(() => "All daemons synched with blockchain");
                break;
            }
            else
            {
                logger.Debug(() => $"Daemon reports error: {response.Error?.Message}");
            }

            if(!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected virtual async Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(
        CancellationToken ct)
    {
        var result = await rpc.ExecuteAsync<BlockTemplate>(logger,
            BitcoinCommands.GetBlockTemplate, ct, extraPoolConfig?.GBTArgs ?? (object) GetBlockTemplateParams());

        return result;
    }

    protected RpcResponse<BlockTemplate> GetBlockTemplateFromJson(string json)
    {
        var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

        return new RpcResponse<BlockTemplate>(result!.ResultAs<BlockTemplate>());
    }

    private BitcoinJob CreateJob()
    {
        return new();
    }

    protected override void PostChainIdentifyConfigure()
    {
        base.PostChainIdentifyConfigure();

        if(DirectCoinbasePayoutEnabled)
        {
            if(network != Network.Main && network != Network.TestNet &&
               network != Network.RegTest)
                throw new PoolStartupException(
                    $"Pool '{poolConfig.Id}' direct SOLO coinbase payout supports only Bitcoin mainnet, testnet and regtest",
                    poolConfig.Id);

            try
            {
                directCoinbaseRecipients = BitcoinDirectCoinbase.ValidateRecipients(
                    poolConfig.RewardRecipients,
                    address => ResolveDirectPayoutDestination(address,
                        network));
            }
            catch(Exception ex) when(ex is not PoolStartupException)
            {
                throw new PoolStartupException(
                    $"Pool '{poolConfig.Id}' has an invalid direct SOLO coinbase recipient contract: {ex.Message}",
                    poolConfig.Id, ex);
            }

            logger.Info(() =>
                $"Direct Bitcoin SOLO coinbase payout is enabled with {directCoinbaseRecipients.Length} positive fee/donation output(s)");
        }

        if(poolConfig.EnableInternalStratum == true && coin.HeaderHasherValue is IHashAlgorithmInit hashInit)
        {
            if(!hashInit.DigestInit(poolConfig))
                logger.Error(()=> $"{hashInit.GetType().Name} initialization failed");
        }
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            // may happen if daemon is currently not connected to peers
            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            var isNew = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                        blockTemplate.Height > job.BlockTemplate?.Height));

            if(isNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(isNew || forceUpdate)
            {
                if(DirectCoinbasePayoutEnabled)
                {
                    try
                    {
                        BitcoinDirectCoinbase.ValidateTemplateAmount(
                            blockTemplate.CoinbaseValue,
                            directCoinbaseRecipients);
                    }
                    catch(PoolStartupException)
                    {
                        throw;
                    }
                    catch(Exception ex)
                    {
                        throw new PoolStartupException(
                            $"Pool '{poolConfig.Id}' cannot construct a valid direct SOLO coinbase at template height {blockTemplate.Height}: {ex.Message}",
                            poolConfig.Id, ex);
                    }
                }

                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue);

                if(isNew)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;
            }

            return (isNew, forceUpdate);
        }

        catch(OperationCanceledException)
        {
            // ignored
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    public override BitcoinJob GetJobForStratum()
    {
        var job = currentJob;
        return job;
    }

    public bool DirectCoinbasePayoutEnabled =>
        extraPoolConfig?.SoloCoinbasePayout == true;

    protected override IDestination AddressToDestination(string address,
        BitcoinAddressType? addressType)
    {
        // The pool address is retained for startup validation and historical
        // custodial blocks. In canonical direct mode, use Bitcoin Core's full
        // network address surface (including native SegWit) even though new
        // direct jobs do not pay this destination.
        if(DirectCoinbasePayoutEnabled)
            return ResolveDirectPayoutDestination(address, network);

        return base.AddressToDestination(address, addressType);
    }

    public async Task<IDestination> ValidateDirectPayoutAddressAsync(
        string address, CancellationToken ct)
    {
        if(!DirectCoinbasePayoutEnabled || string.IsNullOrWhiteSpace(address) ||
           address.Length > 128)
            return null;

        IDestination destination;
        try
        {
            destination = ResolveDirectPayoutDestination(address, network);
        }

        catch(PoolStartupException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if(directCoinbaseRecipients.Any(x => string.Equals(x.ScriptPubKey,
               destination.ScriptPubKey.ToHex(),
               StringComparison.OrdinalIgnoreCase)))
            return null;

        return await ValidateAddressAsync(address, ct) ? destination : null;
    }

    internal static IDestination ResolveDirectPayoutDestination(
        string address, Network expectedNetwork)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(expectedNetwork);

        return BitcoinAddress.Create(address, expectedNetwork);
    }

    public BitcoinJob GetDirectJobForStratum(string minerAddress,
        IDestination minerDestination)
    {
        if(!DirectCoinbasePayoutEnabled)
            throw new InvalidOperationException(
                "Direct SOLO coinbase payout is not enabled");
        ArgumentException.ThrowIfNullOrWhiteSpace(minerAddress);
        ArgumentNullException.ThrowIfNull(minerDestination);

        var source = currentJob ?? throw new InvalidOperationException(
            "No Bitcoin block template is available");
        var directTemplate = new BitcoinDirectCoinbaseTemplate
        {
            MinerAddress = minerAddress,
            MinerDestination = minerDestination,
            MinerScriptPubKey = minerDestination.ScriptPubKey.ToHex(),
            Recipients = directCoinbaseRecipients,
        };
        BitcoinDirectCoinbase.EnsureMinerIsDistinct(directTemplate);

        var job = CreateJob();
        job.InitDirect(source.BlockTemplate, NextJobId(),
            poolConfig, extraPoolConfig, clusterConfig, clock,
            poolAddressDestination, network, isPoS, ShareMultiplier,
            coin.CoinbaseHasherValue, coin.HeaderHasherValue,
            !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ??
                coin.BlockHasherValue,
            directTemplate);
        return job;
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        if(extraPoolConfig?.MaxActiveJobs.HasValue == true)
            maxActiveJobs = extraPoolConfig.MaxActiveJobs.Value;

        base.Configure(pc, cc);
    }

    public virtual object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            BitcoinConstants.ExtranoncePlaceHolderLength - ExtranonceBytes,
        };

        return responseData;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams || submitParams.Length < 5)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // extract params
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

        BitcoinJob job;

        lock(context)
        {
            job = context.GetJob(jobId);
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce, versionBits);

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = job.DirectPayoutAddress ?? context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        // if block candidate, submit & check if accepted by network
        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

            var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                logger.Info(() => job.DirectCoinbaseSettlement != null
                    ? $"Daemon accepted direct-SOLO block {share.BlockHeight} [{share.BlockHash}]"
                    : $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                // persist the coinbase transaction-hash to allow the payment processor
                // to verify later on that the pool has received the reward for the block
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;

                if(job.DirectCoinbaseSettlement != null &&
                   !share.BlockRecordEmitted)
                {
                    await PersistAcceptedCandidateWithoutAccountingAsync(share);
                    share.BlockRecordEmitted = true;
                    ClearDirectSettlementEvidence(share);
                }

                // Direct candidates cross their durable database/journal boundary
                // before refresh observers or other telemetry can fail.
                OnBlockFound();
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;
                ClearDirectSettlementEvidence(share);
            }
        }

        if(poolConfig.PaymentProcessing?.Enabled == true &&
           poolConfig.PaymentProcessing.PayoutScheme == PayoutScheme.PPS)
        {
            share.AccountingId = Miningcore.Mining.ShareAccounting.CreateId();
            share.AccountingRole = ShareAccountingRole.Single;
            share.RewardBasisSatoshis = job.RewardBasisSatoshis;
            share.PreserveCreated = true;

            await AttachPpsEvidencePreservingAcceptedCandidateAsync(poolConfig,
                share);
        }

        return share;
    }

    protected virtual async Task PersistAcceptedCandidateWithoutAccountingAsync(
        Share share)
    {
        if(blockCandidateRecorder == null)
            throw new InvalidOperationException(
                "A synchronous block-candidate recorder is required for direct SOLO or PPS");

        var candidate = CreateAcceptedCandidateWithoutAccounting(share);
        await blockCandidateRecorder.PersistBlockCandidateAsync(candidate);
    }

    protected internal virtual async Task
        AttachPpsEvidencePreservingAcceptedCandidateAsync(PoolConfig pool,
            Share share)
    {
        // Daemon acceptance is already conclusive. Persist the accepted block and its
        // ordinary statistical proof before constructing or publishing any PPS evidence so
        // relay loss, process failure, or a later accounting rejection cannot suppress the
        // only durable candidate record.
        if(share.IsBlockCandidate && !share.BlockRecordEmitted)
        {
            await PersistAcceptedCandidateWithoutAccountingAsync(share);
            share.BlockRecordEmitted = true;
        }

        Miningcore.Mining.ShareAccounting.AttachPpsCreditEvidence(pool, share);
    }

    internal static Share CreateAcceptedCandidateWithoutAccounting(Share share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if(!share.IsBlockCandidate)
            throw new ArgumentException("An accepted block candidate is required",
                nameof(share));

        return new Share
        {
            PoolId = share.PoolId,
            Miner = share.Miner,
            Worker = share.Worker,
            UserAgent = share.UserAgent,
            IpAddress = share.IpAddress,
            Source = share.Source,
            Difficulty = share.Difficulty,
            ShareDifficulty = share.ShareDifficulty,
            ActualDifficulty = share.ActualDifficulty,
            SessionId = share.SessionId,
            BlockHeight = share.BlockHeight,
            BlockReward = share.BlockReward,
            BlockRewardDouble = share.BlockRewardDouble,
            BlockHash = share.BlockHash,
            BlockType = "bitcoin-direct",
            BlockOnly = true,
            IsBlockCandidate = true,
            TransactionConfirmationData = share.TransactionConfirmationData,
            SettlementMode = share.SettlementMode,
            GrossRewardSatoshis = share.GrossRewardSatoshis,
            DirectMinerRewardSatoshis = share.DirectMinerRewardSatoshis,
            DirectMinerScriptPubKey = share.DirectMinerScriptPubKey,
            DirectRecipientOutputs = share.DirectRecipientOutputs,
            NetworkDifficulty = share.NetworkDifficulty,
            PreserveCreated = true,
            Created = share.Created,
        };
    }

    private static void ClearDirectSettlementEvidence(Share share)
    {
        share.SettlementMode = null;
        share.GrossRewardSatoshis = null;
        share.DirectMinerRewardSatoshis = null;
        share.DirectMinerScriptPubKey = null;
        share.DirectRecipientOutputs = null;
    }

    public double ShareMultiplier => coin.ShareMultiplier;

    #endregion // API-Surface
}
