using System.Collections.Concurrent;
using System.Globalization;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Bitcoin;

[CoinFamily(CoinFamily.Bitcoin, CoinFamily.Nexa)]
public class BitcoinPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    internal enum BlockActivity
    {
        Active,
        Inactive,
        Unavailable,
    }

    public BitcoinPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus,
        IActiveBlockGracePeriodTracker activeBlockGracePeriodTracker) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);
        Contract.RequiresNonNull(activeBlockGracePeriodTracker);

        this.ctx = ctx;
        this.activeBlockGracePeriodTracker = activeBlockGracePeriodTracker;
    }

    protected readonly IComponentContext ctx;
    private readonly IActiveBlockGracePeriodTracker activeBlockGracePeriodTracker;
    protected RpcClient rpcClient;
    protected BitcoinPoolConfigExtra extraPoolConfig;
    protected BitcoinDaemonEndpointConfigExtra extraPoolEndpointConfig;
    protected BitcoinPoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    private int payoutDecimalPlaces = 4;
    private CoinTemplate coin;
    private int minConfirmations;
    private static readonly TimeSpan UncertainBlockLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PayoutVerificationRetryDelay = TimeSpan.FromSeconds(1);
    private const int MinimumDefinitiveMisses = 3;
    private const int PayoutVerificationAttempts = 3;

    internal static bool IsUnknownWalletSubmission(JsonRpcError error) =>
        WalletSubmissionOutcome.IsUnknown(error);

    protected virtual Task<RpcResponse<string>> SendManyAsync(object[] args,
        CancellationToken ct) =>
        rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendMany, ct, args);

    protected virtual Task<RpcResponse<string>> SendToAddressAsync(object[] args,
        CancellationToken ct) =>
        rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendToAddress, ct, args);

    protected virtual Task<RpcResponse<JToken>> UnlockWalletAsync(string password,
        CancellationToken ct) =>
        rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct,
            new object[]
            {
                password,
                5, // unlock for N seconds
            });

    protected virtual Task<RpcResponse<JToken>> GetMempoolEntryAsync(string txId,
        CancellationToken ct) =>
        rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.GetMempoolEntry, ct,
            new object[] { txId });

    protected virtual Task<RpcResponse<Transaction>> GetWalletTransactionAsync(string txId,
        CancellationToken ct) =>
        rpcClient.ExecuteAsync<Transaction>(logger, BitcoinCommands.GetTransaction, ct,
            new object[] { txId });

    protected virtual Task DelayPayoutVerificationAsync(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, ct);

    // Acceptance checks share one additional budget once persistence completes and shutdown is observed.
    protected virtual TimeSpan PostCancellationVerificationGracePeriod =>
        TimeSpan.FromSeconds(15);

    protected override string LogCategory => "Bitcoin Payout Handler";

    #region IPayoutHandler

    public virtual Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolEndpointConfig = pc.Extra.SafeExtensionDataAs<BitcoinDaemonEndpointConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        coin = poolConfig.Template.As<CoinTemplate>();
        if(coin is BitcoinTemplate bitcoinTemplate)
        {
            minConfirmations = extraPoolConfig?.MinimumConfirmations ??
                bitcoinTemplate.CoinbaseMinConfimations ??
                BitcoinConstants.CoinbaseMinConfimations;
            payoutDecimalPlaces = bitcoinTemplate.PayoutDecimalPlaces ?? 4;
        }
        else
            minConfirmations = extraPoolConfig?.MinimumConfirmations ??
                BitcoinConstants.CoinbaseMinConfimations;

        logger = LogUtil.GetPoolScopedLogger(typeof(BitcoinPayoutHandler), pc);

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpcClient = new RpcClient(pc.Daemons.First(), jsonSerializerSettings, messageBus, pc.Id);

        return Task.CompletedTask;
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            var directBlocks = page.Where(IsDirectCoinbaseSettlement)
                .ToArray();
            foreach(var directBlock in directBlocks)
            {
                try
                {
                    var classified = await ClassifyDirectCoinbaseBlockAsync(
                        directBlock, ct);
                    if(classified)
                        result.Add(directBlock);
                }
                catch(Exception ex) when(IsMalformedDirectDaemonData(ex))
                {
                    directBlock.Status = BlockStatus.Quarantined;
                    if(!string.Equals(directBlock.DirectSubmissionState,
                           BitcoinDirectSubmission.LegacyObserved,
                           StringComparison.Ordinal))
                        directBlock.DirectSubmissionState =
                            BitcoinDirectSubmission.Quarantined;
                    directBlock.DirectSettlementLastChecked = clock.Now;
                    directBlock.NotifyBlockFoundOnUpdate = false;
                    directBlock.NotifyBlockConfirmationProgressOnUpdate = false;
                    directBlock.NotifyBlockUnlockedOnUpdate = false;
                    result.Add(directBlock);
                    logger.Error(ex, () =>
                        $"[{LogCategory}] Quarantined direct SOLO block {directBlock.BlockHeight} [{directBlock.Hash}] because its immutable settlement evidence could not be verified");
                }
            }

            page = page.Where(block => !IsDirectCoinbaseSettlement(block))
                .ToArray();
            if(page.Length == 0)
                continue;

            var resolvedAuxiliaryBlocks = new HashSet<Block>();

            foreach(var block in page)
            {
                string blockHash;
                string claimedParentBlock = null;
                var definitiveMisses = 0;
                var claimMissKind = AuxPowBlockConfirmation.ClaimMissKind.Absence;
                var isAcceptedMarker = AuxPowBlockConfirmation.TryGetPendingBlockHash(
                    block.TransactionConfirmationData, out blockHash, out definitiveMisses);
                var isAuxPowClaim = !isAcceptedMarker && AuxPowBlockConfirmation.TryGetClaim(
                    block.TransactionConfirmationData, out blockHash, out claimedParentBlock,
                    out definitiveMisses, out claimMissKind);
                var isParentUncertain = !isAcceptedMarker && !isAuxPowClaim &&
                    AuxPowBlockConfirmation.TryGetParentUncertain(
                        block.TransactionConfirmationData, out blockHash, out definitiveMisses);

                if(!isAcceptedMarker && !isAuxPowClaim && !isParentUncertain)
                    continue;

                var response = await GetBlockAsync(blockHash, ct);
                var blockIsKnown = IsExpectedBlock(response, blockHash);
                var blockIsActive = IsActiveBlock(response, blockHash);

                if(blockIsKnown && !blockIsActive && response.Response?.Confirmations < 0)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);
                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{blockHash}] is known by the daemon but is not active");

                    if(!isAuxPowClaim && !isParentUncertain)
                        block.NotifyBlockUnlockedOnUpdate = true;

                    continue;
                }

                if(isAuxPowClaim && blockIsActive)
                {
                    var acceptedParentBlock = response.Response?.AuxPow?.ParentBlock;
                    if(string.IsNullOrEmpty(acceptedParentBlock))
                    {
                        var nextMiss = claimMissKind == AuxPowBlockConfirmation.ClaimMissKind.MissingProof
                            ? definitiveMisses + 1
                            : 1;

                        if(nextMiss >= MinimumDefinitiveMisses &&
                            clock.Now - block.Created >= UncertainBlockLifetime)
                        {
                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            result.Add(block);
                            logger.Warn(() => $"[{LogCategory}] DOGE block {block.BlockHeight} [{blockHash}] repeatedly did not expose auxpow.parentblock; expiring claim");
                        }
                        else
                        {
                            block.TransactionConfirmationData =
                                AuxPowBlockConfirmation.CreateClaim(blockHash,
                                    claimedParentBlock, nextMiss,
                                    AuxPowBlockConfirmation.ClaimMissKind.MissingProof);
                            result.Add(block);
                            logger.Warn(() => $"[{LogCategory}] DOGE block {block.BlockHeight} [{blockHash}] did not expose auxpow.parentblock; claim remains pending after proof-miss {nextMiss}");
                        }

                        continue;
                    }

                    if(!string.Equals(acceptedParentBlock, claimedParentBlock,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);
                        logger.Info(() => $"[{LogCategory}] AuxPoW claim for block {block.BlockHeight} [{blockHash}] lost to a different parent proof");
                        continue;
                    }

                    var finalized = await GetFinalizedAuxPowBlockAsync(block.PoolId, blockHash, ct);
                    if(finalized != null && finalized.Id != block.Id)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);
                        logger.Info(() => $"[{LogCategory}] AuxPoW claim for block {block.BlockHeight} [{blockHash}] superseded by finalized block record {finalized.Id}");
                        continue;
                    }

                    block.Type = "auxpow";
                    block.NotifyBlockFoundOnUpdate = true;
                }

                var coinbaseTransaction = blockIsActive
                    ? response.Response?.Transactions?.FirstOrDefault()
                    : null;

                if(string.IsNullOrEmpty(coinbaseTransaction))
                {
                    var error = response.Error?.Message ?? "block or coinbase transaction is not available yet";
                    logger.Warn(() => $"[{LogCategory}] Unable to reconcile auxiliary block {block.BlockHeight} [{blockHash}]: {error}");

                    if(blockIsActive)
                    {
                        if(isAuxPowClaim)
                        {
                            block.TransactionConfirmationData =
                                AuxPowBlockConfirmation.CreatePending(blockHash);
                            result.Add(block);
                            logger.Warn(() => $"[{LogCategory}] AuxPoW claim for active block {block.BlockHeight} [{blockHash}] matched the accepted parent proof but coinbase transaction data is unavailable; finalized marker remains pending");
                        }
                        else if(isAcceptedMarker && definitiveMisses > 0)
                        {
                            block.TransactionConfirmationData =
                                AuxPowBlockConfirmation.CreatePending(blockHash);
                            result.Add(block);
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{blockHash}] is active; reset historical absence miss count while coinbase transaction data remains unavailable");
                        }
                        else if(isParentUncertain && definitiveMisses > 0)
                        {
                            block.TransactionConfirmationData =
                                AuxPowBlockConfirmation.CreateParentUncertain(blockHash);
                            result.Add(block);
                            logger.Info(() => $"[{LogCategory}] Parent block {block.BlockHeight} [{blockHash}] is active; reset historical absence miss count while coinbase transaction data remains unavailable");
                        }

                        continue;
                    }

                    if((isAcceptedMarker || isAuxPowClaim || isParentUncertain) &&
                        response.Error?.Code == -5)
                    {
                        var nextMiss = isAuxPowClaim &&
                            claimMissKind != AuxPowBlockConfirmation.ClaimMissKind.Absence
                                ? 1
                                : definitiveMisses + 1;

                        if(nextMiss >= MinimumDefinitiveMisses &&
                            clock.Now - block.Created >= UncertainBlockLifetime)
                        {
                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            logger.Info(() => $"[{LogCategory}] Uncertain block {block.BlockHeight} [{blockHash}] expired after {nextMiss} definitive misses");

                            if(isAcceptedMarker)
                                block.NotifyBlockUnlockedOnUpdate = true;
                        }
                        else
                        {
                            block.TransactionConfirmationData = isAcceptedMarker
                                ? AuxPowBlockConfirmation.CreatePending(blockHash, nextMiss)
                                : isAuxPowClaim
                                    ? AuxPowBlockConfirmation.CreateClaim(blockHash,
                                        claimedParentBlock, nextMiss)
                                    : AuxPowBlockConfirmation.CreateParentUncertain(blockHash, nextMiss);
                            logger.Info(() => $"[{LogCategory}] Uncertain block {block.BlockHeight} [{blockHash}] remains pending after definitive miss {nextMiss}");
                        }

                        result.Add(block);
                    }

                    continue;
                }

                if(isParentUncertain)
                {
                    block.Type = "merged-parent";
                    block.NotifyBlockFoundOnUpdate = true;
                }

                block.TransactionConfirmationData = coinbaseTransaction;
                resolvedAuxiliaryBlocks.Add(block);
                result.Add(block);
                logger.Info(() => $"[{LogCategory}] Reconciled accepted auxiliary block {block.BlockHeight} [{blockHash}] to coinbase transaction {coinbaseTransaction}");
            }

            var classifiablePage = page
                .Where(block => !resolvedAuxiliaryBlocks.Contains(block) &&
                    !AuxPowBlockConfirmation.TryGetPendingBlockHash(
                        block.TransactionConfirmationData, out _) &&
                    !AuxPowBlockConfirmation.TryGetClaim(
                        block.TransactionConfirmationData, out _, out _, out _) &&
                    !AuxPowBlockConfirmation.TryGetParentUncertain(
                        block.TransactionConfirmationData, out _, out _))
                .ToArray();

            if(classifiablePage.Length == 0)
                continue;

            // build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
            var results = await GetTransactionsAsync(classifiablePage, ct);

            for(var j = 0; j < results.Length; j++)
            {
                var cmdResult = results[j];

                var transactionInfo = cmdResult.Response?.ToObject<Transaction>();
                var block = classifiablePage[j];

                // check error
                if(cmdResult.Error != null)
                {
                    // Code -5 interpreted as "orphaned"
                    if(cmdResult.Error.Code == -5)
                    {
                        var activity = SupportsActiveBlockGrace(block)
                            ? await GetBlockActivityAsync(block.Hash, ct)
                            : BlockActivity.Inactive;

                        if(activity == BlockActivity.Active)
                        {
                            result.Add(block);
                            logger.Warn(() => $"[{LogCategory}] Wallet has not indexed coinbase {block.TransactionConfirmationData}; block {block.Hash} remains active");
                            ClearUnavailableActiveBlockGrace(block);
                            continue;
                        }

                        if(activity == BlockActivity.Unavailable)
                        {
                            result.Add(block);
                            logger.Warn(() => $"[{LogCategory}] Unable to verify whether block {block.Hash} remains active after wallet lookup failed; keeping block {block.BlockHeight} pending");
                            NotifyUnavailableActiveBlockGrace(block, "wallet lookup failed");
                            continue;
                        }

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);
                        ClearUnavailableActiveBlockGrace(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to daemon error {cmdResult.Error.Code}");

                        block.NotifyBlockUnlockedOnUpdate = true;
                    }

                    else
                        logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {block.TransactionConfirmationData}");
                }

                // missing transaction details are interpreted as "orphaned"
                else if(transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                {
                    var activity = SupportsActiveBlockGrace(block)
                        ? await GetBlockActivityAsync(block.Hash, ct)
                        : BlockActivity.Inactive;

                    if(activity == BlockActivity.Active)
                    {
                        result.Add(block);
                        logger.Warn(() => $"[{LogCategory}] Wallet returned no details for coinbase {block.TransactionConfirmationData}; block {block.Hash} remains active");
                        ClearUnavailableActiveBlockGrace(block);
                        continue;
                    }

                    if(activity == BlockActivity.Unavailable)
                    {
                        result.Add(block);
                        logger.Warn(() => $"[{LogCategory}] Unable to verify whether block {block.Hash} remains active after wallet returned no transaction details; keeping block {block.BlockHeight} pending");
                        NotifyUnavailableActiveBlockGrace(block, "wallet returned no transaction details");
                        continue;
                    }

                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);
                    ClearUnavailableActiveBlockGrace(block);

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to missing tx details");

                    block.NotifyBlockUnlockedOnUpdate = true;
                }

                else
                {
                    switch(transactionInfo.Details[0].Category)
                    {
                        case "immature":
                            ClearUnavailableActiveBlockGrace(block);

                            // update progress
                            block.ConfirmationProgress = Math.Min(1.0d, (double) transactionInfo.Confirmations / minConfirmations);
                            block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                            block.NotifyBlockConfirmationProgressOnUpdate = true;
                            result.Add(block);

                            break;

                        case "generate":
                            ClearUnavailableActiveBlockGrace(block);

                            // matured and spendable coinbase transaction
                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;
                            block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                            block.NotifyBlockUnlockedOnUpdate = true;
                            result.Add(block);

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                            break;

                        default:
                            ClearUnavailableActiveBlockGrace(block);

                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned. Category: {transactionInfo.Details[0].Category}");

                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            result.Add(block);

                            block.NotifyBlockUnlockedOnUpdate = true;
                            break;
                    }
                }
            }
        }

        return result.ToArray();
    }

    internal static bool IsDirectCoinbaseSettlement(Block block) =>
        string.Equals(block?.SettlementMode,
            BitcoinDirectCoinbaseSettlement.Mode,
            StringComparison.Ordinal);

    protected virtual Task<RpcResponse<JToken>> GetDirectSettlementBlockAsync(
        string blockHash, CancellationToken ct) =>
        rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.GetBlock, ct,
            new object[] { blockHash, 2 });

    protected virtual Task<RpcResponse<JToken>> SubmitDirectSettlementBlockAsync(
        string blockHex, CancellationToken ct) =>
        rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.SubmitBlock, ct,
            new object[] { blockHex });

    internal async Task<bool> ClassifyDirectCoinbaseBlockAsync(Block block,
        CancellationToken ct)
    {
        ValidatePersistedDirectSettlement(block);
        ValidatePersistedDirectRecipientEvidence(block);
        var originalStatus = block.Status;
        var wasPending = originalStatus == BlockStatus.Pending;
        block.NotifyBlockFoundOnUpdate = false;
        block.NotifyBlockConfirmationProgressOnUpdate = false;
        block.NotifyBlockUnlockedOnUpdate = false;

        RpcResponse<JToken> response;
        if(BitcoinDirectSubmission.RequiresReplay(
               block.DirectSubmissionState))
        {
            var submit = await SubmitDirectSettlementBlockAsync(
                block.DirectSubmissionBlock, ct);
            response = await GetDirectSettlementBlockAsync(block.Hash, ct);

            if(IsActiveDirectSettlementResponse(response, block.Hash))
            {
                // Validate while the persisted replay projection still has its
                // canonical prepared/uncertain counters. Mutating a prepared
                // row first would make its own state validator reject the
                // successful replay as malformed.
                try
                {
                    VerifyDirectCoinbaseTransaction(block,
                        (JObject) response.Response);
                }
                catch(Exception ex) when(IsMalformedDirectDaemonData(ex))
                {
                    block.DirectSubmissionAttempts = checked(
                        block.DirectSubmissionAttempts.GetValueOrDefault() + 1);
                    block.DirectSubmissionLastAttempt = clock.Now;
                    block.DirectSubmissionState =
                        BitcoinDirectSubmission.SubmittedUncertain;
                    block.Status = BlockStatus.Pending;
                    logger.Warn(ex,
                        $"[{LogCategory}] Direct SOLO block " +
                        $"{block.BlockHeight} [{block.Hash}] remains " +
                        "replayable because the daemon returned malformed " +
                        "active-block data");
                    return true;
                }
                block.DirectSubmissionAttempts = checked(
                    block.DirectSubmissionAttempts.GetValueOrDefault() + 1);
                block.DirectSubmissionLastAttempt = clock.Now;
                block.DirectSubmissionState =
                    BitcoinDirectSubmission.ObservedActive;
                block.NotifyBlockFoundOnUpdate = true;
            }
            else
            {
                block.DirectSubmissionAttempts = checked(
                    block.DirectSubmissionAttempts.GetValueOrDefault() + 1);
                block.DirectSubmissionLastAttempt = clock.Now;
                var submitError = submit.Error?.Message ??
                    submit.Error?.Code.ToString(CultureInfo.InvariantCulture) ??
                    submit.Response?.ToString();
                var definitiveMiss = response.Error?.Code == -5 &&
                    !string.IsNullOrWhiteSpace(submitError) &&
                    submit.Error?.Code != -500 &&
                    !BitcoinJobManagerBase<BitcoinJob>
                        .IsDuplicateBlockSubmissionResponse(submitError) &&
                    !BitcoinJobManagerBase<BitcoinJob>
                        .IsInconclusiveBlockSubmissionResponse(submitError);

                if(definitiveMiss)
                {
                    block.DirectSubmissionDefinitiveMisses = checked(
                        block.DirectSubmissionDefinitiveMisses
                            .GetValueOrDefault() + 1);
                }

                if(definitiveMiss &&
                   block.DirectSubmissionDefinitiveMisses >=
                       BitcoinDirectSubmission.MinimumDefinitiveMisses &&
                   clock.Now - block.Created >=
                       BitcoinDirectSubmission.UncertainLifetime)
                {
                    block.DirectSubmissionState =
                        BitcoinDirectSubmission.Rejected;
                    block.Status = BlockStatus.Orphaned;
                    block.ConfirmationProgress = 0;
                    block.Reward = 0;
                    logger.Warn(() =>
                        $"[{LogCategory}] Direct SOLO block " +
                        $"{block.BlockHeight} [{block.Hash}] was rejected after " +
                        $"{block.DirectSubmissionDefinitiveMisses} definitive " +
                        "submission misses");
                }
                else
                {
                    block.DirectSubmissionState =
                        BitcoinDirectSubmission.SubmittedUncertain;
                    block.Status = BlockStatus.Pending;
                    logger.Warn(() =>
                        $"[{LogCategory}] Direct SOLO block " +
                        $"{block.BlockHeight} [{block.Hash}] remains replayable " +
                        "after an inconclusive durable submission attempt");
                }

                return true;
            }
        }
        else
            response = await GetDirectSettlementBlockAsync(block.Hash, ct);
        if(response.Error != null)
        {
            if(response.Error.Code == -5)
            {
                block.DirectSettlementLastChecked = clock.Now;
                block.Status = BlockStatus.Orphaned;
                block.ConfirmationProgress = 0;
                block.Reward = 0;
                block.NotifyBlockUnlockedOnUpdate =
                    originalStatus != BlockStatus.Orphaned;
                logger.Info(() =>
                    $"[{LogCategory}] Direct SOLO block {block.BlockHeight} [{block.Hash}] is no longer known to the active chain");
                return true;
            }

            logger.Warn(() =>
                $"[{LogCategory}] Unable to inspect direct SOLO block {block.BlockHeight} [{block.Hash}]: {response.Error.Message}");
            if(wasPending)
                return false;

            // Throttle an unavailable daemon response for an already-confirmed
            // settlement. Its terminal status is unchanged and it remains eligible
            // for a later bounded reconciliation pass.
            block.DirectSettlementLastChecked = clock.Now;
            return true;
        }

        JObject document;
        int confirmations;
        try
        {
            document = response.Response as JObject ??
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} returned a malformed getblock response");
            var returnedHash = document.Value<string>("hash");
            if(!string.Equals(returnedHash, block.Hash,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} getblock hash does not match its persisted candidate");

            confirmations = document.Value<int?>("confirmations") ??
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} has no confirmation count");
        }
        catch(Exception ex) when(IsMalformedDirectDaemonData(ex))
        {
            logger.Warn(ex,
                $"[{LogCategory}] Deferred classification of direct SOLO " +
                $"block {block.BlockHeight} [{block.Hash}] because the daemon " +
                "returned malformed block data");
            if(wasPending)
                return false;

            block.DirectSettlementLastChecked = clock.Now;
            return true;
        }
        if(confirmations < 0)
        {
            block.DirectSettlementLastChecked = clock.Now;
            block.Status = BlockStatus.Orphaned;
            block.ConfirmationProgress = 0;
            block.Reward = 0;
            block.NotifyBlockUnlockedOnUpdate =
                originalStatus != BlockStatus.Orphaned;
            return true;
        }

        try
        {
            VerifyDirectCoinbaseTransaction(block, document);
        }
        catch(Exception ex) when(IsMalformedDirectDaemonData(ex))
        {
            logger.Warn(ex,
                $"[{LogCategory}] Deferred classification of direct SOLO " +
                $"block {block.BlockHeight} [{block.Hash}] because the daemon " +
                "returned malformed coinbase data");
            if(wasPending)
                return false;

            block.DirectSettlementLastChecked = clock.Now;
            return true;
        }

        if(string.Equals(block.DirectSubmissionState,
               BitcoinDirectSubmission.Rejected,
               StringComparison.Ordinal))
        {
            block.DirectSubmissionState =
                BitcoinDirectSubmission.ObservedActive;
            block.NotifyBlockFoundOnUpdate = true;
        }

        block.DirectSettlementLastChecked = clock.Now;
        block.Reward = block.GrossRewardSatoshis.Value /
            BitcoinConstants.SatoshisPerBitcoin;
        block.ConfirmationProgress = Math.Min(1d,
            (double) confirmations / minConfirmations);
        block.Status = confirmations >= minConfirmations
            ? BlockStatus.Confirmed
            : BlockStatus.Pending;
        block.NotifyBlockConfirmationProgressOnUpdate =
            block.Status == BlockStatus.Pending;

        if(confirmations >= minConfirmations)
        {
            block.ConfirmationProgress = 1;
            block.NotifyBlockUnlockedOnUpdate =
                originalStatus != BlockStatus.Confirmed;
            logger.Info(() =>
                $"[{LogCategory}] Confirmed direct SOLO block {block.BlockHeight} with on-chain miner and fee settlement");
        }

        return true;
    }

    internal static void ValidatePersistedDirectSettlement(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if(!string.Equals(block.Type,
               BitcoinDirectCoinbaseSettlement.BlockType,
               StringComparison.Ordinal) ||
           !string.Equals(block.SettlementMode,
               BitcoinDirectCoinbaseSettlement.Mode,
               StringComparison.Ordinal) ||
           block.GrossRewardSatoshis is not > 0 ||
           block.DirectMinerRewardSatoshis is not > 0 ||
           block.DirectMinerRewardSatoshis > block.GrossRewardSatoshis ||
           string.IsNullOrWhiteSpace(block.DirectMinerScriptPubKey) ||
           string.IsNullOrWhiteSpace(block.DirectRecipientOutputs) ||
           string.IsNullOrWhiteSpace(block.Hash) ||
           string.IsNullOrWhiteSpace(block.TransactionConfirmationData))
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has incomplete persisted settlement evidence");

        if(!IsCanonicalLowerHex(block.DirectMinerScriptPubKey) ||
           block.Hash.Length != 64 ||
           block.TransactionConfirmationData.Length != 64)
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has malformed persisted settlement identity");

        BitcoinDirectSubmission.ValidatePersistedProjection(block);
    }

    private static bool IsActiveDirectSettlementResponse(
        RpcResponse<JToken> response, string expectedHash)
    {
        try
        {
            return response?.Error == null &&
                response.Response is JObject document &&
                string.Equals(document.Value<string>("hash"), expectedHash,
                    StringComparison.OrdinalIgnoreCase) &&
                document.Value<int?>("confirmations") is >= 0;
        }
        catch(Exception ex) when(IsMalformedDirectDaemonData(ex))
        {
            return false;
        }
    }

    private static bool IsMalformedDirectDaemonData(Exception ex) =>
        ex is InvalidDataException or JsonException or OverflowException or
            FormatException or InvalidCastException;

    internal static void VerifyDirectCoinbaseTransaction(Block block,
        JObject document)
    {
        ValidatePersistedDirectSettlement(block);

        var coinbase = (document["tx"] as JArray)?.FirstOrDefault() as
            JObject ?? throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has no decoded coinbase transaction");
        if(!string.Equals(coinbase.Value<string>("txid"),
               block.TransactionConfirmationData,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} coinbase transaction id differs from accepted-candidate evidence");

        var expected = GetExpectedDirectCoinbaseOutputs(block);
        var actual = new List<(string Script, long Amount)>();
        foreach(var output in coinbase["vout"] as JArray ?? new JArray())
        {
            var value = output.Value<decimal?>("value") ??
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} contains an output without a value");
            var scaled = value * BitcoinConstants.SatoshisPerBitcoin;
            if(scaled != decimal.Truncate(scaled) || scaled < 0 ||
               scaled > long.MaxValue)
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} contains a non-satoshi output value");
            var amount = (long) scaled;
            if(amount == 0)
                continue;

            var script = output["scriptPubKey"]?.Value<string>("hex");
            if(!IsCanonicalLowerHex(script))
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} contains a malformed output script");
            actual.Add((script, amount));
        }

        var orderedExpected = expected.OrderBy(x => x.Script,
            StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Amount).ToArray();
        var orderedActual = actual.OrderBy(x => x.Script,
            StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Amount).ToArray();
        var outputsMatch = orderedExpected.Length == orderedActual.Length &&
            orderedExpected.Zip(orderedActual).All(pair =>
                string.Equals(pair.First.Script, pair.Second.Script,
                    StringComparison.OrdinalIgnoreCase) &&
                pair.First.Amount == pair.Second.Amount);
        if(!outputsMatch ||
           SumAmounts(actual) != block.GrossRewardSatoshis)
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} coinbase outputs do not match the immutable accepted settlement");
    }

    private static void ValidatePersistedDirectRecipientEvidence(Block block) =>
        _ = GetExpectedDirectCoinbaseOutputs(block);

    private static List<(string Script, long Amount)>
        GetExpectedDirectCoinbaseOutputs(Block block)
    {
        BitcoinDirectCoinbaseOutput[] recipients;
        try
        {
            recipients = JsonConvert.DeserializeObject<
                BitcoinDirectCoinbaseOutput[]>(
                block.DirectRecipientOutputs) ??
                Array.Empty<BitcoinDirectCoinbaseOutput>();
        }
        catch(JsonException ex)
        {
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} recipient evidence is not valid JSON",
                ex);
        }

        if(recipients.Any(x => x == null ||
               string.IsNullOrWhiteSpace(x.Address) ||
               !IsCanonicalLowerHex(x.ScriptPubKey) ||
               x.AmountSatoshis < BitcoinDirectCoinbase.MinimumOutputSatoshis))
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} contains malformed recipient evidence");

        var expected = new List<(string Script, long Amount)>
        {
            (block.DirectMinerScriptPubKey,
                block.DirectMinerRewardSatoshis.Value),
        };
        expected.AddRange(recipients.Select(x =>
            (x.ScriptPubKey, x.AmountSatoshis)));

        if(expected.Select(x => x.Script).Distinct(
               StringComparer.OrdinalIgnoreCase).Count() != expected.Count ||
           SumAmounts(expected) != block.GrossRewardSatoshis)
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} persisted output evidence violates its exact settlement contract");

        return expected;
    }

    private static bool IsCanonicalLowerHex(string value) =>
        !string.IsNullOrEmpty(value) && value.Length % 2 == 0 &&
        value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static long SumAmounts(
        IEnumerable<(string Script, long Amount)> outputs)
    {
        long result = 0;
        foreach(var output in outputs)
            result = checked(result + output.Amount);
        return result;
    }

    protected virtual Task<RpcResponse<DaemonBlock>> GetBlockAsync(string blockHash,
        CancellationToken ct)
    {
        return rpcClient.ExecuteAsync<DaemonBlock>(logger, BitcoinCommands.GetBlock, ct,
            new object[] { blockHash });
    }

    protected virtual Task<RpcResponse<JToken>[]> GetTransactionsAsync(Block[] blocks,
        CancellationToken ct)
    {
        var batch = blocks.Select(block => new RpcRequest(BitcoinCommands.GetTransaction,
            new[] { block.TransactionConfirmationData })).ToArray();

        return rpcClient.ExecuteBatchAsync(logger, ct, batch);
    }

    protected virtual Task<Block> GetFinalizedAuxPowBlockAsync(string poolId, string blockHash,
        CancellationToken ct)
    {
        return cf.Run(con => blockRepo.GetBlockByPoolHashAndTypeAsync(con, poolId, blockHash,
            "auxpow"));
    }

    private async Task<BlockActivity> GetBlockActivityAsync(string blockHash, CancellationToken ct)
    {
        var response = await GetBlockAsync(blockHash, ct);
        return ClassifyBlockActivity(response, blockHash);
    }

    internal static BlockActivity ClassifyBlockActivity(RpcResponse<DaemonBlock> response,
        string blockHash)
    {
        if(response?.Error != null || response.Response == null ||
            !string.Equals(response.Response.Hash, blockHash, StringComparison.OrdinalIgnoreCase))
            return BlockActivity.Unavailable;

        if(response.Response.Confirmations < 0)
            return BlockActivity.Inactive;

        return response.Response.Confirmations > 0
            ? BlockActivity.Active
            : BlockActivity.Unavailable;
    }

    private static bool SupportsActiveBlockGrace(Block block)
    {
        return block.Type is "auxpow" or "merged-parent";
    }

    private void NotifyUnavailableActiveBlockGrace(Block block, string reason)
    {
        if(!activeBlockGracePeriodTracker.TryAcquireNotification(block.PoolId, block.Id,
            block.Hash, block.Type, clock.Now, UncertainBlockLifetime))
            return;

        var subject = $"[{poolConfig.Id}] merged-mining block reconciliation delayed";
        var message = $"Pool {poolConfig.Id} block {block.BlockHeight} [{block.Hash}] " +
            $"({block.Type}) has had unavailable active-chain verification for at least " +
            $"{(int) UncertainBlockLifetime.TotalMinutes} minutes because " +
            $"{reason} and the daemon could not verify whether the block is still active. " +
            "Check getblock/gettransaction RPC behaviour, wallet indexing, and any RPC proxy before manually intervening.";

        try
        {
            messageBus.SendMessage(new AdminNotification(subject, message));
            activeBlockGracePeriodTracker.MarkNotificationSent(block.PoolId, block.Id,
                block.Hash, block.Type);
        }

        catch(Exception ex)
        {
            activeBlockGracePeriodTracker.ReleaseNotification(block.PoolId, block.Id,
                block.Hash, block.Type);
            logger.Error(ex, () => $"[{LogCategory}] Unable to emit delayed reconciliation admin notification for block {block.BlockHeight} [{block.Hash}]");
        }
    }

    private void ClearUnavailableActiveBlockGrace(Block block)
    {
        if(SupportsActiveBlockGrace(block))
            activeBlockGracePeriodTracker.Clear(block.PoolId, block.Id, block.Hash, block.Type);
    }

    private static bool IsExpectedBlock(RpcResponse<DaemonBlock> response, string blockHash)
    {
        return response?.Error == null && string.Equals(response.Response?.Hash, blockHash,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveBlock(RpcResponse<DaemonBlock> response, string blockHash)
    {
        return IsExpectedBlock(response, blockHash) && response.Response.Confirmations > 0;
    }

    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        // build args
        var requestedAmounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => x.Amount);
        // Never ask the wallet to pay more than a miner is owed. Both sendmany modes
        // persist and subtract these same truncated values, leaving any sub-precision
        // remainder on the miner balance for a future payout.
        var amounts = requestedAmounts.ToDictionary(x => x.Key,
                x => TruncatePayoutAmount(x.Value, payoutDecimalPlaces))
            .Where(x => x.Value > 0)
            .ToDictionary(x => x.Key, x => x.Value);

        if(amounts.Count == 0)
        {
            if(requestedAmounts.Count > 0)
            {
                logger.Warn(() => $"[{LogCategory}] No payout submitted: all " +
                    $"{requestedAmounts.Count} selected balance(s) are below the configured " +
                    $"payout precision (payoutDecimalPlaces={payoutDecimalPlaces}). " +
                    $"Review minimumPayment");
            }

            return;
        }

        var payableBalances = balances
            .Where(x => amounts.ContainsKey(x.Address))
            .ToArray();
        var precisionSkippedBalances = balances
            .Where(x => x.Amount > 0 && !amounts.ContainsKey(x.Address))
            .ToArray();
        var submittedBalances = payableBalances.Select(x => new Balance
        {
            PoolId = x.PoolId,
            Address = x.Address,
            Amount = amounts[x.Address],
        }).ToArray();

        logger.Info(() => $"[{LogCategory}] Preparing wallet request: " +
            $"{FormatPayoutAmount(amounts.Values.Sum())} to {amounts.Count} payable address(es) " +
            $"from {FormatExactPayoutAmount(requestedAmounts.Values.Sum())} owed across " +
            $"{requestedAmounts.Count} selected balance(s)");

        object[] args;

        var identifier = !string.IsNullOrEmpty(clusterConfig.PaymentProcessing?.CoinbaseString) ?
            clusterConfig.PaymentProcessing.CoinbaseString.Trim() : "Miningcore";

        var comment = $"{identifier} Payment";

        if(!(extraPoolConfig?.HasBrokenSendMany == true || poolConfig.Template is BitcoinTemplate { HasBrokenSendMany: true }))
        {
            if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
            {
                var subtractFeesFrom = amounts.Keys.ToArray();

                if(!poolConfig.Template.As<BitcoinTemplate>().HasMasterNodes)
                {
                    args = new object[]
                    {
                        string.Empty, // default account
                        amounts, // addresses and associated amounts
                        1, // only spend funds covered by this many confirmations
                        comment, // tx comment
                        subtractFeesFrom, // distribute transaction fee equally over all recipients
                    };
                }

                else
                {
                    args = new object[]
                    {
                        string.Empty, // default account
                        amounts, // addresses and associated amounts
                        1, // only spend funds covered by this many confirmations
                        false, // Whether to add confirmations to transactions locked via InstantSend
                        comment, // tx comment
                        subtractFeesFrom, // distribute transaction fee equally over all recipients
                        false, // use_is: Send this transaction as InstantSend
                        false, // Use anonymized funds only
                    };
                }
            }

            else
            {
                args = new object[]
                {
                    string.Empty, // default account
                    amounts, // addresses and associated amounts
                };
            }

            var didUnlockWallet = false;

            // send command
            tryTransfer:
            ct.ThrowIfCancellationRequested();
            var result = await SendManyAsync(args, ct);

            if(result.Error == null)
            {
                if(didUnlockWallet)
                {
                    // lock wallet
                    logger.Info(() => $"[{LogCategory}] Locking wallet");
                    await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletLock, ct);
                }

                // check result
                var txId = result.Response;

                if(string.IsNullOrWhiteSpace(txId))
                {
                    var detail = $"{BitcoinCommands.SendMany} returned success without a " +
                        "transaction id";
                    throw new PayoutOutcomeUncertainException(detail, null,
                        CreateSendManyUncertainReconciliation(payableBalances,
                            precisionSkippedBalances, amounts, null, detail));
                }
                else
                    logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

                try
                {
                    await PersistPaymentsAsync(submittedBalances, txId);
                }
                catch(PayoutOutcomeUncertainException ex)
                {
                    throw new PayoutOutcomeUncertainException(ex.Message,
                        ex.InnerException ?? ex,
                        CreateSendManyUncertainReconciliation(payableBalances,
                            precisionSkippedBalances, amounts, txId, ex.Message));
                }

                try
                {
                    await RunPayoutVerificationAsync(ct, verificationToken =>
                        EnsurePayoutTransactionAcceptedAsync(txId, verificationToken));
                }
                catch(PayoutOutcomeUncertainException ex)
                {
                    throw new PayoutOutcomeUncertainException(ex.Message,
                        ex.InnerException ?? ex,
                        CreateSendManyUncertainReconciliation(payableBalances,
                            precisionSkippedBalances, amounts, txId, ex.Message));
                }

                LogPrecisionSkippedBalances(precisionSkippedBalances);
                TryNotifyPayout(() => NotifyPayoutSuccess(poolConfig.Id, payableBalances, new[]
                {
                    txId
                }, null, amounts.Values.Sum(),
                    amounts.Values.Sum() - payableBalances.Sum(x => x.Amount)), "success");
            }

            else
            {
                if(IsUnknownWalletSubmission(result.Error))
                {
                    var detail = $"{BitcoinCommands.SendMany} outcome is unknown: " +
                        result.Error.Message;
                    throw new PayoutOutcomeUncertainException(detail, null,
                        CreateSendManyUncertainReconciliation(payableBalances,
                            precisionSkippedBalances, amounts, null, detail));
                }

                if(result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResult = await UnlockWalletAsync(
                            extraPoolPaymentProcessingConfig.WalletPassword, ct);
                        ct.ThrowIfCancellationRequested();

                        if(unlockResult.Error == null)
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        var failure = $"{BitcoinCommands.WalletPassphrase} returned error: " +
                            $"{unlockResult.Error.Message} code {unlockResult.Error.Code}";
                        logger.Error(() => $"[{LogCategory}] {failure}");
                        TryNotifyPayout(() => NotifyPayoutFailure(poolConfig.Id,
                            payableBalances, failure, null), "failure");
                        return;
                    }

                    else
                    {
                        const string failure = "Wallet is locked but walletPassword was not " +
                            "configured. Unable to send funds.";
                        logger.Error(() => $"[{LogCategory}] {failure}");
                        TryNotifyPayout(() => NotifyPayoutFailure(poolConfig.Id,
                            payableBalances, failure, null), "failure");
                        return;
                    }
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                    TryNotifyPayout(() => NotifyPayoutFailure(poolConfig.Id, payableBalances,
                        $"{BitcoinCommands.SendMany} returned error: " +
                        $"{result.Error.Message} code {result.Error.Code}", null,
                        amounts.Values.Sum(),
                        amounts.Values.Sum() - payableBalances.Sum(x => x.Amount)), "failure");
                }
            }
        }

        else
        {
            var txFailures = new ConcurrentBag<Tuple<KeyValuePair<string, decimal>, Exception>>();
            var successBalances = new ConcurrentDictionary<Balance, string>();
            var attemptedAddresses = new ConcurrentDictionary<string, byte>();
            Dictionary<Balance, string> persistedBalances = null;
            IReadOnlyDictionary<string, PayoutOutcomeUncertainException>
                acceptanceVerificationFailures =
                    new Dictionary<string, PayoutOutcomeUncertainException>();
            var startedSubmissions = 0;
            var loopCancelled = false;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = ct
            };

            try
            {
                await Parallel.ForEachAsync(amounts, parallelOptions, async (x, _ct) =>
                {
                    var (address, amount) = x;

                    // use a common id for all log entries related to this transfer
                    var transferId = CorrelationIdGenerator.GetNextId();
                    var walletSubmissionStarted = false;

                    await Guard(async () =>
                    {
                        _ct.ThrowIfCancellationRequested();
                        walletSubmissionStarted = true;
                        attemptedAddresses.TryAdd(address, 0);
                        Interlocked.Increment(ref startedSubmissions);
                        logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending " +
                            $"{FormatPayoutAmount(amount)} to {address}");

                        var result = await SendToAddressAsync(new object[]
                        {
                            address,
                            amount,
                        }, _ct);

                        // check result
                        var txId = result.Response;

                        if(result.Error != null)
                        {
                            if(IsUnknownWalletSubmission(result.Error))
                                throw new PayoutOutcomeUncertainException(
                                    $"[{transferId}] {BitcoinCommands.SendToAddress} outcome is unknown: {result.Error.Message}");

                            throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}");
                        }

                        if(string.IsNullOrWhiteSpace(txId))
                            throw new PayoutOutcomeUncertainException(
                                $"[{transferId}] {BitcoinCommands.SendToAddress} returned success without a transaction id");
                        else
                            logger.Info(() => $"[{LogCategory}] [{transferId}] Payment transaction id: {txId}");

                        successBalances.TryAdd(new Balance
                        {
                            PoolId = poolConfig.Id,
                            Address = address,
                            Amount = amount,
                        }, txId);
                    }, ex =>
                    {
                        if(ex is OperationCanceledException && !walletSubmissionStarted)
                            return;

                        if(ex is OperationCanceledException)
                        {
                            ex = new PayoutOutcomeUncertainException(
                                $"[{transferId}] {BitcoinCommands.SendToAddress} was interrupted after wallet submission began; its outcome is unknown",
                                ex);
                        }

                        txFailures.Add(Tuple.Create(x, ex));
                    });
                });
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested &&
                Volatile.Read(ref startedSubmissions) > 0)
            {
                loopCancelled = true;
                // Parallel.ForEachAsync drains active delegates before surfacing cancellation.
                // Continue through persistence and outcome classification so returned transaction
                // ids are never abandoned and interrupted wallet calls retain payout ownership.
                var startedCount = Volatile.Read(ref startedSubmissions);
                logger.Warn(() => $"[{LogCategory}] Payout submission was cancelled after " +
                    $"{startedCount} wallet call(s) started; " +
                    "reconciling completed outcomes before stopping");
            }

            var failedTransfers = txFailures.ToArray();
            var notAttemptedTransfers = amounts
                .Where(x => !attemptedAddresses.ContainsKey(x.Key))
                .ToArray();

            if(successBalances.Any())
            {
                persistedBalances = successBalances.ToDictionary(x => x.Key, x => x.Value);

                EnsureDistinctPayoutTransactionIds(persistedBalances,
                    failedTransfers, notAttemptedTransfers, requestedAmounts,
                    precisionSkippedBalances);

                try
                {
                    await PersistPaymentsAsync(persistedBalances);
                }
                catch(PayoutOutcomeUncertainException ex)
                {
                    throw new PayoutOutcomeUncertainException(ex.Message,
                        ex.InnerException ?? ex,
                        CreateBrokenSendManyReconciliation(persistedBalances,
                            failedTransfers, notAttemptedTransfers,
                            new Dictionary<string, PayoutOutcomeUncertainException>(),
                            requestedAmounts, precisionSkippedBalances, ex.Message));
                }

                await RunPayoutVerificationAsync(ct, async verificationToken =>
                {
                    acceptanceVerificationFailures =
                        await CollectPayoutTransactionAcceptanceFailuresAsync(
                            persistedBalances.Values.Distinct(), verificationToken);
                });
            }

            var uncertainties = failedTransfers
                .Where(x => x.Item2 is PayoutOutcomeUncertainException)
                .Select(x => new PayoutOutcomeUncertainException(
                    $"{x.Item1.Key} {FormatPayoutAmount(x.Item1.Value)} requires reconciliation: " +
                    x.Item2.Message, x.Item2))
                .Concat(acceptanceVerificationFailures.Values)
                .ToArray();
            PayoutOutcomeUncertainException uncertainFailure = null;

            if(uncertainties.Length == 1)
                uncertainFailure = uncertainties[0];
            else if(uncertainties.Length > 1)
            {
                uncertainFailure = new PayoutOutcomeUncertainException(
                    $"{uncertainties.Length} payout outcomes require reconciliation: " +
                    string.Join(" | ", uncertainties.Select(x => x.Message)),
                    new AggregateException(uncertainties));
            }

            Balance[] failureBalances = null;
            string transferFailureDetails = null;

            if(failedTransfers.Length > 0)
            {
                failureBalances = failedTransfers
                    .Select(x => new Balance
                    {
                        PoolId = poolConfig.Id,
                        Address = x.Item1.Key,
                        Amount = requestedAmounts[x.Item1.Key],
                    }).ToArray();
                transferFailureDetails = string.Join(", ", failedTransfers.Select(x =>
                    $"{x.Item1.Key} {FormatPayoutAmount(x.Item1.Value)}: {x.Item2.Message}"));

                logger.Error(() => $"[{LogCategory}] Failed to transfer the following balances: " +
                    transferFailureDetails);
            }

            // PayoutManager emits the single authoritative notification for an uncertain batch.
            // Subset notifications are useful only when every wallet outcome is conclusive.
            if(uncertainFailure != null)
            {
                var conclusiveFailures = failedTransfers
                    .Where(x => x.Item2 is not PayoutOutcomeUncertainException)
                    .Select(x => $"{x.Item1.Key} {FormatPayoutAmount(x.Item1.Value)}: " +
                        x.Item2.Message)
                    .ToArray();

                if(conclusiveFailures.Length > 0)
                {
                    uncertainFailure = new PayoutOutcomeUncertainException(
                        $"{uncertainFailure.Message} | {conclusiveFailures.Length} additional " +
                        $"payout transfer(s) failed conclusively: " +
                        string.Join(" | ", conclusiveFailures), uncertainFailure);
                }

                var reconciliation = CreateBrokenSendManyReconciliation(
                    persistedBalances, failedTransfers, notAttemptedTransfers,
                    acceptanceVerificationFailures, requestedAmounts,
                    precisionSkippedBalances);

                throw new PayoutOutcomeUncertainException(uncertainFailure.Message,
                    uncertainFailure.InnerException ?? uncertainFailure, reconciliation);
            }

            if(persistedBalances != null && acceptanceVerificationFailures.Count == 0)
            {
                var acceptedBalances = persistedBalances.Keys.Select(x => new Balance
                {
                    PoolId = x.PoolId,
                    Address = x.Address,
                    Amount = requestedAmounts[x.Address],
                }).ToArray();
                var submittedAmount = persistedBalances.Keys.Sum(x => x.Amount);

                if(!loopCancelled)
                    LogPrecisionSkippedBalances(precisionSkippedBalances);

                TryNotifyPayout(() => NotifyPayoutSuccess(poolConfig.Id,
                    acceptedBalances, persistedBalances.Values.ToArray(), null,
                    submittedAmount,
                    submittedAmount - acceptedBalances.Sum(x => x.Amount)),
                    "success");
            }

            if(failureBalances != null)
            {
                var submittedAmount = failedTransfers.Sum(x => x.Item1.Value);

                TryNotifyPayout(() => NotifyPayoutFailure(poolConfig.Id, failureBalances,
                    transferFailureDetails, null, submittedAmount,
                    submittedAmount - failureBalances.Sum(x => x.Amount)), "failure");
            }

            if(loopCancelled)
                ct.ThrowIfCancellationRequested();
        }
    }

    private async Task RunPayoutVerificationAsync(CancellationToken shutdownToken,
        Func<CancellationToken, Task> action)
    {
        using var verificationCts = new CancellationTokenSource();
        using var registration = shutdownToken.Register(() =>
        {
            logger.Warn(() => $"[{LogCategory}] Shutdown began during persisted payout " +
                $"verification; allowing up to " +
                $"{PostCancellationVerificationGracePeriod.TotalSeconds:0} additional second(s)");
            verificationCts.CancelAfter(PostCancellationVerificationGracePeriod);
        });

        await action(verificationCts.Token);
    }

    private static PayoutReconciliation CreateSendManyUncertainReconciliation(
        IEnumerable<Balance> balances, IEnumerable<Balance> precisionSkippedBalances,
        IReadOnlyDictionary<string, decimal> submittedAmounts, string txId, string detail)
    {
        return new PayoutReconciliation
        {
            Uncertain = balances
                .Where(x => submittedAmounts.ContainsKey(x.Address))
                .Select(x => new PayoutReconciliationEntry
                {
                    Address = x.Address,
                    Amount = x.Amount,
                    SubmittedAmount = submittedAmounts[x.Address],
                    TransactionId = txId,
                    Detail = detail,
                }).ToArray(),
            NotAttempted = precisionSkippedBalances
                .Select(x => new PayoutReconciliationEntry
                {
                    Address = x.Address,
                    Amount = x.Amount,
                    Detail = "Wallet request omitted because the amount is below wallet precision",
                }).ToArray(),
        };
    }

    internal static decimal TruncatePayoutAmount(decimal amount, int decimalPlaces)
    {
        if(decimalPlaces is < 0 or > 28)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces));

        var increment = 1m;

        for(var i = 0; i < decimalPlaces; i++)
            increment /= 10m;

        return amount - amount % increment;
    }

    private string FormatPayoutAmount(decimal amount)
    {
        var format = payoutDecimalPlaces > 0
            ? $"0.{new string('#', payoutDecimalPlaces)}"
            : "0";
        return $"{amount.ToString(format, CultureInfo.InvariantCulture)} {coin.Symbol}";
    }

    private string FormatExactPayoutAmount(decimal amount)
    {
        return $"{PayoutAmountFormatter.FormatExact(amount)} {coin.Symbol}";
    }

    private void LogPrecisionSkippedBalances(Balance[] precisionSkippedBalances)
    {
        if(precisionSkippedBalances.Length == 0)
            return;

        logger.Debug(() => $"[{LogCategory}] Conclusive payout processing retained " +
            $"{precisionSkippedBalances.Length} precision-skipped balance(s) for a future " +
            $"payout because they are below the configured payout precision " +
            $"(payoutDecimalPlaces={payoutDecimalPlaces}): " +
            string.Join(", ", precisionSkippedBalances.Select(x =>
                $"{x.Address} {FormatExactPayoutAmount(x.Amount)}")));
    }

    private static PayoutReconciliation CreateBrokenSendManyReconciliation(
        IReadOnlyDictionary<Balance, string> submittedBalances,
        Tuple<KeyValuePair<string, decimal>, Exception>[] failedTransfers,
        KeyValuePair<string, decimal>[] notAttemptedTransfers,
        IReadOnlyDictionary<string, PayoutOutcomeUncertainException>
            acceptanceVerificationFailures,
        IReadOnlyDictionary<string, decimal> requestedAmounts,
        IEnumerable<Balance> precisionSkippedBalances,
        string persistenceFailure = null)
    {
        var submitted = submittedBalances ?? new Dictionary<Balance, string>();
        var persistenceFailed = !string.IsNullOrEmpty(persistenceFailure);

        return new PayoutReconciliation
        {
            Accepted = persistenceFailed
                ? Array.Empty<PayoutReconciliationEntry>()
                : submitted
                    .Where(x => !acceptanceVerificationFailures.ContainsKey(x.Value))
                    .Select(x => new PayoutReconciliationEntry
                    {
                        Address = x.Key.Address,
                        Amount = requestedAmounts[x.Key.Address],
                        SubmittedAmount = x.Key.Amount,
                        TransactionId = x.Value,
                    }).ToArray(),
            Failed = failedTransfers
                .Where(x => x.Item2 is not PayoutOutcomeUncertainException)
                .Select(x => new PayoutReconciliationEntry
                {
                    Address = x.Item1.Key,
                    Amount = requestedAmounts[x.Item1.Key],
                    SubmittedAmount = x.Item1.Value,
                    Detail = x.Item2.Message,
                }).ToArray(),
            Uncertain = submitted
                .Where(x => persistenceFailed ||
                    acceptanceVerificationFailures.ContainsKey(x.Value))
                .Select(x => new PayoutReconciliationEntry
                {
                    Address = x.Key.Address,
                    Amount = requestedAmounts[x.Key.Address],
                    SubmittedAmount = x.Key.Amount,
                    TransactionId = x.Value,
                    Detail = persistenceFailed
                        ? persistenceFailure
                        : acceptanceVerificationFailures[x.Value].Message,
                })
                .Concat(failedTransfers
                    .Where(x => x.Item2 is PayoutOutcomeUncertainException)
                    .Select(x => new PayoutReconciliationEntry
                    {
                        Address = x.Item1.Key,
                        Amount = requestedAmounts[x.Item1.Key],
                        SubmittedAmount = x.Item1.Value,
                        Detail = x.Item2.Message,
                    }))
                .ToArray(),
            NotAttempted = notAttemptedTransfers
                .Select(x => new PayoutReconciliationEntry
                {
                    Address = x.Key,
                    Amount = requestedAmounts[x.Key],
                    Detail = "Wallet submission was not started because payout processing was cancelled",
                })
                .Concat(precisionSkippedBalances.Select(x => new PayoutReconciliationEntry
                {
                    Address = x.Address,
                    Amount = x.Amount,
                    Detail = "Wallet request omitted because the amount is below wallet precision",
                }))
                .ToArray(),
        };
    }

    private static void EnsureDistinctPayoutTransactionIds(
        IReadOnlyDictionary<Balance, string> submittedBalances,
        Tuple<KeyValuePair<string, decimal>, Exception>[] failedTransfers,
        KeyValuePair<string, decimal>[] notAttemptedTransfers,
        IReadOnlyDictionary<string, decimal> requestedAmounts,
        IEnumerable<Balance> precisionSkippedBalances)
    {
        var duplicateTransactionIds = submittedBalances.Values
            .GroupBy(x => x, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToArray();

        if(duplicateTransactionIds.Length == 0)
            return;

        var detail = "Separate wallet submissions returned duplicate transaction id(s): " +
            string.Join(", ", duplicateTransactionIds);
        var reconciliation = CreateBrokenSendManyReconciliation(submittedBalances,
            failedTransfers, notAttemptedTransfers,
            new Dictionary<string, PayoutOutcomeUncertainException>(), requestedAmounts,
            precisionSkippedBalances, detail);

        throw new PayoutOutcomeUncertainException(detail, null, reconciliation);
    }

    private void TryNotifyPayout(Action notify, string outcome)
    {
        try
        {
            notify();
        }
        catch(Exception ex)
        {
            logger.Error(ex, () =>
                $"[{LogCategory}] Unable to emit payout {outcome} notification");
        }
    }

    private async Task<IReadOnlyDictionary<string, PayoutOutcomeUncertainException>>
        CollectPayoutTransactionAcceptanceFailuresAsync(IEnumerable<string> txIds,
        CancellationToken ct)
    {
        var failures = new Dictionary<string, PayoutOutcomeUncertainException>();

        foreach(var txId in txIds)
        {
            try
            {
                await EnsurePayoutTransactionAcceptedAsync(txId, ct);
            }
            catch(PayoutOutcomeUncertainException ex)
            {
                failures[txId] = ex;
            }
        }

        return failures;
    }

    private async Task EnsurePayoutTransactionAcceptedAsync(string txId, CancellationToken ct)
    {
        var attempt = 0;

        try
        {
            for(attempt = 1; ; attempt++)
            {
                var mempoolResponse = await GetMempoolEntryAsync(txId, ct);
                string mempoolStatus;

                if(mempoolResponse.Error == null)
                {
                    // Local mempool membership is sufficient acceptance evidence. Bitcoin Core's
                    // unbroadcast flag only means that no peer has acknowledged initial relay yet;
                    // the node retains the transaction and continues attempting relay.
                    if(mempoolResponse.Response is JObject)
                        return;

                    mempoolStatus = $"{BitcoinCommands.GetMempoolEntry} returned no valid entry";
                }
                else
                {
                    if(mempoolResponse.Error.Code == (int) BitcoinRPCErrorCode.RPC_METHOD_NOT_FOUND)
                    {
                        logger.Warn(() => $"[{LogCategory}] Daemon does not support {BitcoinCommands.GetMempoolEntry}; " +
                            $"cannot verify local acceptance of payout transaction {txId}");
                        return;
                    }

                    mempoolStatus = $"{BitcoinCommands.GetMempoolEntry} returned {mempoolResponse.Error.Code}: " +
                        mempoolResponse.Error.Message;
                }

                var walletResponse = await GetWalletTransactionAsync(txId, ct);

                if(walletResponse.Error == null && walletResponse.Response?.Confirmations > 0)
                    return;

                var verificationError = walletResponse.Error != null
                    ? $"{BitcoinCommands.GetTransaction} returned {walletResponse.Error.Code}: {walletResponse.Error.Message}"
                    : $"wallet confirmations: {walletResponse.Response?.Confirmations ?? 0}";

                if(attempt >= PayoutVerificationAttempts)
                {
                    throw new PayoutOutcomeUncertainException(
                        $"Wallet accepted payout transaction {txId}, but it remained absent from the local mempool " +
                        $"and unconfirmed after {PayoutVerificationAttempts} verification attempts " +
                        $"({mempoolStatus}; {verificationError}). Its payment record was persisted to prevent a " +
                        "duplicate payout. Confirm wallet broadcasting is enabled, then broadcast or reconcile " +
                        "the transaction manually");
                }

                logger.Warn(() => $"[{LogCategory}] Payout transaction {txId} could not be verified on " +
                    $"attempt {attempt} of {PayoutVerificationAttempts} ({mempoolStatus}; {verificationError}); " +
                    $"retrying in {PayoutVerificationRetryDelay.TotalSeconds:0} second(s)");

                await DelayPayoutVerificationAsync(PayoutVerificationRetryDelay, ct);
            }
        }
        catch(OperationCanceledException ex)
        {
            // Once a payout is persisted, interrupted verification is financially uncertain.
            // Preserve that classification so PayoutManager retains durable ownership.
            throw new PayoutOutcomeUncertainException(
                $"Payout verification for persisted transaction {txId} was interrupted after " +
                $"attempt {attempt} of {PayoutVerificationAttempts}", ex);
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler
}
