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
using Miningcore.Notifications.Messages;
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
    private bool bip54CoinbaseEnabled;
    private bool directCoinbasePayoutEnabled;
    private BitcoinDirectCoinbaseRecipient[] directCoinbaseRecipients =
        Array.Empty<BitcoinDirectCoinbaseRecipient>();
    internal bool? CachedBip54CoinbasePolicy => poolConfig != null &&
        coin != null && BitcoinJob.IsCanonicalBitcoin(poolConfig, coin)
            ? bip54CoinbaseEnabled
            : null;
    internal event Action<Exception> DirectJobConstructionFailed;

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
                        BitcoinJob.ValidateBlockTemplateTransactionWeights(
                            blockTemplate, network);
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

                job.InitWithCoinbasePolicy(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue,
                    bip54CoinbaseEnabled);

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

        catch(PoolStartupException)
        {
            throw;
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

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        await base.PostStartInitAsync(ct);

        if(DirectCoinbasePayoutEnabled)
            await ReplayPreparedDirectSubmissionsAsync(ct);
    }

    protected internal async Task ReplayPreparedDirectSubmissionsAsync(
        CancellationToken ct)
    {
        if(blockCandidateRecorder == null)
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' direct SOLO requires its durable submission recorder",
                poolConfig.Id);

        const int pageSize = 32;
        var afterId = 0L;
        var replayed = 0;

        while(true)
        {
            var blocks = await blockCandidateRecorder
                .GetDirectBlockSubmissionsForReplayAsync(poolConfig.Id,
                    afterId, pageSize, ct) ??
                Array.Empty<Miningcore.Persistence.Model.Block>();
            if(blocks.Length == 0)
                break;

            foreach(var block in blocks)
            {
                ct.ThrowIfCancellationRequested();
                afterId = Math.Max(afterId, block.Id);
                try
                {
                    BitcoinDirectSubmission.ValidatePersistedBlock(block);
                }
                catch(InvalidDataException ex)
                {
                    var quarantined = await blockCandidateRecorder
                        .QuarantineDirectBlockSubmissionAsync(block.Id, ct);
                    if(quarantined != null &&
                       BitcoinDirectSubmission.RequiresReplay(
                           quarantined.DirectSubmissionState))
                        throw new PoolStartupException(
                            $"Pool '{poolConfig.Id}' could not isolate malformed " +
                            $"direct-SOLO replay evidence for block " +
                            $"{block.BlockHeight} [{block.Hash}]",
                            poolConfig.Id, ex);

                    logger.Error(ex, () =>
                        $"Quarantined malformed direct-SOLO replay evidence " +
                        $"for block {block.BlockHeight} [{block.Hash}] before " +
                        "opening Stratum");
                    continue;
                }

                var share = CreateReplayShare(block);
                BitcoinDirectSubmissionOutcome outcome;
                try
                {
                    var result = await SubmitDirectBlockAsync(share,
                        block.DirectSubmissionBlock, ct);
                    outcome = (result.Accepted || result.Duplicate) &&
                        string.Equals(result.CoinbaseTx,
                            block.TransactionConfirmationData,
                            StringComparison.OrdinalIgnoreCase)
                        ? BitcoinDirectSubmissionOutcome.ObservedActive
                        : result.Ambiguous || result.Accepted ||
                            result.Duplicate
                            ? BitcoinDirectSubmissionOutcome.Ambiguous
                            : BitcoinDirectSubmissionOutcome.DefinitiveMiss;
                }
                catch(Exception ex)
                {
                    logger.Warn(ex, () =>
                        $"Startup replay of direct-SOLO block " +
                        $"{block.BlockHeight} [{block.Hash}] was inconclusive");
                    outcome = BitcoinDirectSubmissionOutcome.Ambiguous;
                }

                await RecordDirectSubmissionOutcomeSafelyAsync(share, outcome);
                replayed++;
            }

            if(blocks.Length < pageSize)
                break;
        }

        if(replayed > 0)
            logger.Warn(() =>
                $"Replayed {replayed} durable direct-SOLO submission " +
                "outbox entr" + (replayed == 1 ? "y" : "ies") +
                " before opening Stratum");
    }

    private static Share CreateReplayShare(
        Miningcore.Persistence.Model.Block block) =>
        new()
        {
            PoolId = block.PoolId,
            Miner = block.Miner,
            Source = block.Source,
            BlockHeight = checked((long) block.BlockHeight),
            BlockHash = block.Hash,
            BlockType = block.Type,
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = block.TransactionConfirmationData,
            SettlementMode = block.SettlementMode,
            GrossRewardSatoshis = block.GrossRewardSatoshis,
            DirectMinerRewardSatoshis = block.DirectMinerRewardSatoshis,
            DirectMinerScriptPubKey = block.DirectMinerScriptPubKey,
            DirectRecipientOutputs = block.DirectRecipientOutputs,
            DirectSubmissionState = block.DirectSubmissionState,
            DirectSubmissionBlock = block.DirectSubmissionBlock,
            DirectSubmissionAttempts = block.DirectSubmissionAttempts,
            DirectSubmissionDefinitiveMisses =
                block.DirectSubmissionDefinitiveMisses,
            DirectSubmissionLastAttempt = block.DirectSubmissionLastAttempt,
            Created = block.Created,
        };

    public bool DirectCoinbasePayoutEnabled =>
        directCoinbasePayoutEnabled;

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
        IDestination minerDestination, long authorizationGeneration)
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
            AuthorizationGeneration = authorizationGeneration,
            MinerAddress = minerAddress,
            MinerDestination = minerDestination,
            MinerScriptPubKey = minerDestination.ScriptPubKey.ToHex(),
            Recipients = directCoinbaseRecipients,
        };

        BitcoinDirectCoinbase.EnsureMinerIsDistinct(directTemplate);

        try
        {
            var job = CreateJob();
            job.InitDirect(source.BlockTemplate, NextJobId(),
                poolConfig, extraPoolConfig, clusterConfig, clock,
                poolAddressDestination, network, isPoS, ShareMultiplier,
                coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ??
                    coin.BlockHasherValue,
                directTemplate, bip54CoinbaseEnabled);
            return job;
        }
        catch(Exception ex) when(ex is InvalidDataException or
            OverflowException)
        {
            DirectJobConstructionFailed?.Invoke(new PoolStartupException(
                $"Pool '{poolConfig.Id}' cannot construct a consensus-valid " +
                $"direct SOLO job at height {source.BlockTemplate.Height}: " +
                ex.Message, poolConfig.Id, ex));
            throw;
        }
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);

        directCoinbasePayoutEnabled = BitcoinPoolConfigPolicy
            .ResolveSoloCoinbasePayout(pc, extraPoolConfig,
                extraPoolConfigBindingError);

        if(BitcoinJob.IsCanonicalBitcoin(pc, coin))
        {
            bip54CoinbaseEnabled = BitcoinPoolConfigPolicy
                .ResolveBip54Coinbase(pc, extraPoolConfig,
                    extraPoolConfigBindingError);
            if(bip54CoinbaseEnabled)
                logger.Info(() => "Canonical Bitcoin coinbase policy: BIP 54-compatible " +
                    "locktime/sequence fields enabled; value-bearing outputs precede " +
                    "the BIP 141 witness commitment");
            else
                logger.Warn(() => "Canonical Bitcoin coinbase policy: compatibility " +
                    "fallback enabled by bip54Coinbase=false; legacy locktime/sequence " +
                    "fields and witness-first output order are in use");
        }
        else if(string.Equals(coin.Symbol, "BTC", StringComparison.Ordinal))
        {
            logger.Warn(() => $"Pool '{pc.Id}' uses BTC template '{pc.Coin}' without " +
                "the canonical Bitcoin identity; retaining legacy coinbase fields and " +
                "witness-output order");
        }
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

        var job = context.GetJob(jobId);

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

            var isDirectCoinbase = job.DirectCoinbaseSettlement != null;
            var acceptResponse = isDirectCoinbase
                ? await PersistAndSubmitDirectCandidateAsync(share, blockHex, ct)
                : await SubmitBlockAsync(share, blockHex, ct);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted ||
                (isDirectCoinbase && acceptResponse.Ambiguous);

            if(acceptResponse.Accepted)
            {
                logger.Info(() => isDirectCoinbase
                    ? $"Daemon accepted direct-SOLO block {share.BlockHeight} [{share.BlockHash}]"
                    : $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                // persist the coinbase transaction-hash to allow the payment processor
                // to verify later on that the pool has received the reward for the block
                if(!isDirectCoinbase)
                    share.TransactionConfirmationData = acceptResponse.CoinbaseTx;

                // Direct candidates cross their durable database/journal boundary
                // before refresh observers or other telemetry can fail.
                OnBlockFound();
            }

            else if(isDirectCoinbase && acceptResponse.Ambiguous)
            {
                logger.Warn(() => $"Direct-SOLO block submission outcome for " +
                    $"block {share.BlockHeight} [{share.BlockHash}] is uncertain; " +
                    "the pre-submission durable record will be reconciled");
                OnBlockFound();
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;
            }

            if(isDirectCoinbase)
                ClearDirectSettlementEvidence(share);
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

    protected virtual async Task PersistCandidateWithoutAccountingAsync(
        Share share)
    {
        if(blockCandidateRecorder == null)
            throw new InvalidOperationException(
                "A synchronous block-candidate recorder is required for direct SOLO or PPS");

        var candidate = CreateCandidateWithoutAccounting(share);
        await blockCandidateRecorder.PersistBlockCandidateAsync(candidate);
    }

    protected virtual Task<SubmitResult> SubmitDirectBlockAsync(Share share,
        string blockHex, CancellationToken ct) =>
        SubmitBlockAsync(share, blockHex, ct, false);

    protected virtual Task OnDirectSubmissionPreparedAsync(Share candidate,
        CancellationToken ct) => Task.CompletedTask;

    protected async Task<SubmitResult> PersistAndSubmitDirectCandidateAsync(
        Share share, string blockHex, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(share.TransactionConfirmationData))
        {
            throw new InvalidDataException(
                "A direct SOLO candidate requires its locally calculated coinbase transaction ID before submission");
        }

        if(blockCandidateRecorder == null)
            throw new InvalidOperationException(
                "A synchronous block-candidate recorder is required for direct SOLO");

        var localCoinbaseTx = share.TransactionConfirmationData;
        var candidate = CreateCandidateWithoutAccounting(share, blockHex);
        var preparation = await blockCandidateRecorder
            .PersistDirectBlockSubmissionAsync(candidate) ??
            new DirectBlockSubmissionPreparation();
        share.BlockRecordEmitted = true;
        await OnDirectSubmissionPreparedAsync(candidate, ct);

        try
        {
            var result = await SubmitDirectBlockAsync(share, blockHex, ct);

            var exactActiveSubmission =
                (result.Accepted || result.Duplicate) && string.Equals(
                    result.CoinbaseTx, localCoinbaseTx,
                    StringComparison.OrdinalIgnoreCase);

            if((result.Accepted || result.Duplicate) &&
               !exactActiveSubmission)
            {
                logger.Error(() => $"Daemon returned coinbase transaction " +
                    $"'{result.CoinbaseTx ?? "<missing>"}' for direct-SOLO block " +
                    $"{share.BlockHeight} [{share.BlockHash}], but Miningcore " +
                    $"serialized '{localCoinbaseTx}'; durable reconciliation queued");
                SendDirectSubmissionNotification(
                    "Direct SOLO block submission requires reconciliation",
                    $"Pool {share.PoolId} submitted block {share.BlockHeight} " +
                    $"[{share.BlockHash}], but the daemon did not return the " +
                    "locally calculated coinbase transaction ID. The durable " +
                    "candidate remains pending for reconciliation.");
                await RecordDirectSubmissionOutcomeSafelyAsync(candidate,
                    BitcoinDirectSubmissionOutcome.Ambiguous);
                return new SubmitResult(false, localCoinbaseTx, true,
                    result.Duplicate);
            }

            var outcome = exactActiveSubmission
                ? BitcoinDirectSubmissionOutcome.ObservedActive
                : result.Ambiguous
                    ? BitcoinDirectSubmissionOutcome.Ambiguous
                    : BitcoinDirectSubmissionOutcome.DefinitiveMiss;
            await RecordDirectSubmissionOutcomeSafelyAsync(candidate, outcome);

            return exactActiveSubmission && result.Duplicate
                ? new SubmitResult(true, localCoinbaseTx, false, true)
                : result;
        }
        catch(Exception ex)
        {
            logger.Error(ex, () => $"Direct-SOLO submission outcome for block " +
                $"{share.BlockHeight} [{share.BlockHash}] could not be classified; " +
                "the pre-submission durable record will be reconciled");
            SendDirectSubmissionNotification(
                "Direct SOLO block submission outcome is uncertain",
                $"Pool {share.PoolId} could not classify submission of block " +
                $"{share.BlockHeight} [{share.BlockHash}] after persisting its " +
                "settlement evidence. The durable candidate remains pending for " +
                "reconciliation.");
            await RecordDirectSubmissionOutcomeSafelyAsync(candidate,
                BitcoinDirectSubmissionOutcome.Ambiguous);
            return new SubmitResult(false, localCoinbaseTx, true);
        }
        finally
        {
            await blockCandidateRecorder
                .CompleteDirectBlockSubmissionPreparationAsync(candidate,
                    preparation);
        }
    }

    private async Task RecordDirectSubmissionOutcomeSafelyAsync(
        Share candidate, BitcoinDirectSubmissionOutcome outcome)
    {
        try
        {
            await blockCandidateRecorder.RecordDirectBlockSubmissionAttemptAsync(
                candidate, outcome, clock.Now);
        }
        catch(Exception ex)
        {
            logger.Error(ex, () =>
                $"Could not persist direct-SOLO submission state for block " +
                $"{candidate.BlockHeight} [{candidate.BlockHash}]; the durable " +
                "prepared payload remains replayable");
        }
    }

    private void SendDirectSubmissionNotification(string subject,
        string message)
    {
        try
        {
            messageBus.SendMessage(new AdminNotification(subject, message));
        }
        catch(Exception ex)
        {
            logger.Error(ex,
                "Failed to publish a direct-SOLO submission-reconciliation notification");
        }
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
            await PersistCandidateWithoutAccountingAsync(share);
            share.BlockRecordEmitted = true;
        }

        Miningcore.Mining.ShareAccounting.AttachPpsCreditEvidence(pool, share);
    }

    internal static Share CreateCandidateWithoutAccounting(Share share,
        string directSubmissionBlock = null)
    {
        ArgumentNullException.ThrowIfNull(share);
        if(!share.IsBlockCandidate)
            throw new ArgumentException("A locally validated block candidate is required",
                nameof(share));

        var result = new Share
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
            BlockType = string.Equals(share.SettlementMode,
                BitcoinDirectCoinbaseSettlement.Mode,
                StringComparison.Ordinal)
                ? BitcoinDirectCoinbaseSettlement.BlockType
                : "bitcoin-direct",
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

        if(string.Equals(result.BlockType,
               BitcoinDirectCoinbaseSettlement.BlockType,
               StringComparison.Ordinal))
        {
            result.DirectSubmissionState = BitcoinDirectSubmission.Prepared;
            result.DirectSubmissionBlock = directSubmissionBlock;
            result.DirectSubmissionAttempts = 0;
            result.DirectSubmissionDefinitiveMisses = 0;
            BitcoinDirectSubmission.ValidatePreparedShare(result);
        }

        return result;
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
