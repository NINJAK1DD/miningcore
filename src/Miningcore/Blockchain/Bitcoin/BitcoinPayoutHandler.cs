using System.Collections.Concurrent;
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
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? bitcoinTemplate.CoinbaseMinConfimations ?? BitcoinConstants.CoinbaseMinConfimations;
            payoutDecimalPlaces = bitcoinTemplate.PayoutDecimalPlaces ?? 4;
        }
        else
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? BitcoinConstants.CoinbaseMinConfimations;

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
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => Math.Round(x.Amount, payoutDecimalPlaces));

        if(amounts.Count == 0)
            return;

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

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

                if(string.IsNullOrEmpty(txId))
                    throw new PayoutOutcomeUncertainException(
                        $"{BitcoinCommands.SendMany} returned success without a transaction id");
                else
                    logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

                await PersistPaymentsAsync(balances, txId);
                await EnsurePayoutTransactionAcceptedAsync(txId, ct);

                TryNotifyPayout(() => NotifyPayoutSuccess(poolConfig.Id, balances, new[]
                {
                    txId
                }, null), "success");
            }

            else
            {
                if(IsUnknownWalletSubmission(result.Error))
                    throw new PayoutOutcomeUncertainException(
                        $"{BitcoinCommands.SendMany} outcome is unknown: {result.Error.Message}");

                if(result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResult = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                        {
                            extraPoolPaymentProcessingConfig.WalletPassword,
                            (object) 5 // unlock for N seconds
                        });

                        if(unlockResult.Error == null)
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        else
                            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {result.Error.Message} code {result.Error.Code}");
                    }

                    else
                        logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                    NotifyPayoutFailure(poolConfig.Id, balances, $"{BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}", null);
                }
            }
        }

        else
        {
            var txFailures = new ConcurrentBag<Tuple<KeyValuePair<string, decimal>, Exception>>();
            var successBalances = new ConcurrentDictionary<Balance, string>();
            Dictionary<Balance, string> persistedBalances = null;
            PayoutOutcomeUncertainException acceptanceVerificationFailure = null;
            var startedSubmissions = 0;

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

                    await Guard(async () =>
                    {
                        logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending {FormatAmount(amount)} to {address}");
                        Interlocked.Increment(ref startedSubmissions);

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

                        if(string.IsNullOrEmpty(txId))
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
                // Parallel.ForEachAsync drains active delegates before surfacing cancellation.
                // Continue through persistence and outcome classification so returned transaction
                // ids are never abandoned and interrupted wallet calls retain payout ownership.
                logger.Warn(() => $"[{LogCategory}] Payout submission was cancelled after " +
                    $"{Volatile.Read(ref startedSubmissions)} wallet call(s) started; " +
                    "reconciling completed outcomes before stopping");
            }

            if(successBalances.Any())
            {
                persistedBalances = successBalances.ToDictionary(x => x.Key, x => x.Value);

                await PersistPaymentsAsync(persistedBalances);

                try
                {
                    await EnsurePayoutTransactionsAcceptedAsync(
                        persistedBalances.Values.Distinct(), ct);
                }
                catch(PayoutOutcomeUncertainException ex)
                {
                    acceptanceVerificationFailure = ex;
                }
            }

            var failedTransfers = txFailures.ToArray();
            var walletSubmissionFailure = failedTransfers
                .Select(x => x.Item2)
                .OfType<PayoutOutcomeUncertainException>()
                .FirstOrDefault();
            PayoutOutcomeUncertainException uncertainFailure;

            if(walletSubmissionFailure != null && acceptanceVerificationFailure != null)
            {
                uncertainFailure = new PayoutOutcomeUncertainException(
                    "Multiple payout outcomes require reconciliation: " +
                    $"{walletSubmissionFailure.Message} | {acceptanceVerificationFailure.Message}",
                    new AggregateException(walletSubmissionFailure, acceptanceVerificationFailure));
            }
            else
                uncertainFailure = walletSubmissionFailure ?? acceptanceVerificationFailure;

            // Classify the financial outcome before emitting any best-effort notifications.
            // A subscriber failure must never replace an already-detected uncertain outcome.
            if(persistedBalances != null && acceptanceVerificationFailure == null)
            {
                TryNotifyPayout(() => NotifyPayoutSuccess(poolConfig.Id,
                    persistedBalances.Keys.ToArray(), persistedBalances.Values.ToArray(), null),
                    "success");
            }

            if(failedTransfers.Length > 0)
            {
                var failureBalances = failedTransfers
                    .Select(x => new Balance { Amount = x.Item1.Value }).ToArray();
                var error = string.Join(", ", failedTransfers.Select(x =>
                    $"{x.Item1.Key} {FormatAmount(x.Item1.Value)}: {x.Item2.Message}"));

                logger.Error(() => $"[{LogCategory}] Failed to transfer the following balances: {error}");

                TryNotifyPayout(() => NotifyPayoutFailure(poolConfig.Id, failureBalances,
                    error, null), "failure");
            }

            if(uncertainFailure != null)
                throw uncertainFailure;
        }
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

    private async Task EnsurePayoutTransactionsAcceptedAsync(IEnumerable<string> txIds,
        CancellationToken ct)
    {
        var failures = new List<PayoutOutcomeUncertainException>();

        foreach(var txId in txIds)
        {
            try
            {
                await EnsurePayoutTransactionAcceptedAsync(txId, ct);
            }
            catch(PayoutOutcomeUncertainException ex)
            {
                failures.Add(ex);
            }
        }

        if(failures.Count == 1)
            throw failures[0];

        if(failures.Count > 1)
        {
            throw new PayoutOutcomeUncertainException(
                $"Payout acceptance verification failed for {failures.Count} persisted transactions: " +
                string.Join(" | ", failures.Select(x => x.Message)),
                new AggregateException(failures));
        }
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
