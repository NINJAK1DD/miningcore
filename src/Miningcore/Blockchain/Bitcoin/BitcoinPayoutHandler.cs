using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
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
    public BitcoinPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
    }

    protected readonly IComponentContext ctx;
    protected RpcClient rpcClient;
    protected BitcoinPoolConfigExtra extraPoolConfig;
    protected BitcoinDaemonEndpointConfigExtra extraPoolEndpointConfig;
    protected BitcoinPoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    private int payoutDecimalPlaces = 4;
    private CoinTemplate coin;
    private int minConfirmations;
    private static readonly TimeSpan UncertainBlockLifetime = TimeSpan.FromMinutes(30);
    private const int MinimumDefinitiveMisses = 3;

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
                var isAcceptedMarker = AuxPowBlockConfirmation.TryGetPendingBlockHash(
                    block.TransactionConfirmationData, out blockHash);
                var isAuxPowClaim = !isAcceptedMarker && AuxPowBlockConfirmation.TryGetClaim(
                    block.TransactionConfirmationData, out blockHash, out claimedParentBlock,
                    out definitiveMisses);
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
                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    continue;
                }

                var coinbaseTransaction = blockIsActive
                    ? response.Response?.Transactions?.FirstOrDefault()
                    : null;

                if(string.IsNullOrEmpty(coinbaseTransaction))
                {
                    var error = response.Error?.Message ?? "block or coinbase transaction is not available yet";
                    logger.Warn(() => $"[{LogCategory}] Unable to reconcile auxiliary block {block.BlockHeight} [{blockHash}]: {error}");

                    if((isAuxPowClaim || isParentUncertain) && response.Error?.Code == -5)
                    {
                        var nextMiss = definitiveMisses + 1;

                        if(nextMiss >= MinimumDefinitiveMisses &&
                            clock.Now - block.Created >= UncertainBlockLifetime)
                        {
                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            logger.Info(() => $"[{LogCategory}] Uncertain block {block.BlockHeight} [{blockHash}] expired after {nextMiss} definitive misses");
                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        }
                        else
                        {
                            block.TransactionConfirmationData = isAuxPowClaim
                                ? AuxPowBlockConfirmation.CreateClaim(blockHash,
                                    claimedParentBlock, nextMiss)
                                : AuxPowBlockConfirmation.CreateParentUncertain(blockHash, nextMiss);
                            logger.Info(() => $"[{LogCategory}] Uncertain block {block.BlockHeight} [{blockHash}] remains pending after definitive miss {nextMiss}");
                        }

                        result.Add(block);
                    }

                    continue;
                }

                if(isAuxPowClaim)
                {
                    var acceptedParentBlock = response.Response?.AuxPow?.ParentBlock;
                    if(string.IsNullOrEmpty(acceptedParentBlock))
                    {
                        logger.Warn(() => $"[{LogCategory}] DOGE block {block.BlockHeight} [{blockHash}] did not expose auxpow.parentblock");
                        continue;
                    }

                    if(!string.Equals(acceptedParentBlock, claimedParentBlock,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);
                        logger.Info(() => $"[{LogCategory}] AuxPoW claim for block {block.BlockHeight} [{blockHash}] lost to a different parent proof");
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        continue;
                    }

                    var finalized = await GetFinalizedAuxPowBlockAsync(block.PoolId, blockHash, ct);
                    if(finalized != null && finalized.Id != block.Id)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);
                        logger.Info(() => $"[{LogCategory}] AuxPoW claim for block {block.BlockHeight} [{blockHash}] superseded by finalized block record {finalized.Id}");
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        continue;
                    }

                    block.Type = "auxpow";
                }
                else if(isParentUncertain)
                    block.Type = "merged-parent";

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
                        if(block.Type == "auxpow" && await IsBlockActiveAsync(block.Hash, ct))
                        {
                            result.Add(block);
                            logger.Warn(() => $"[{LogCategory}] Wallet has not indexed AuxPoW coinbase {block.TransactionConfirmationData}; child block {block.Hash} remains active");
                            continue;
                        }

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to daemon error {cmdResult.Error.Code}");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }

                    else
                        logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {block.TransactionConfirmationData}");
                }

                // missing transaction details are interpreted as "orphaned"
                else if(transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                {
                    if(block.Type == "auxpow" && await IsBlockActiveAsync(block.Hash, ct))
                    {
                        result.Add(block);
                        logger.Warn(() => $"[{LogCategory}] Wallet returned no details for AuxPoW coinbase {block.TransactionConfirmationData}; child block {block.Hash} remains active");
                        continue;
                    }

                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to missing tx details");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }

                else
                {
                    switch(transactionInfo.Details[0].Category)
                    {
                        case "immature":
                            // update progress
                            block.ConfirmationProgress = Math.Min(1.0d, (double) transactionInfo.Confirmations / minConfirmations);
                            block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                            result.Add(block);

                            messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                            break;

                        case "generate":
                            // matured and spendable coinbase transaction
                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;
                            block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                            result.Add(block);

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            break;

                        default:
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned. Category: {transactionInfo.Details[0].Category}");

                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            result.Add(block);

                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
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

    private async Task<bool> IsBlockActiveAsync(string blockHash, CancellationToken ct)
    {
        var response = await GetBlockAsync(blockHash, ct);
        return IsActiveBlock(response, blockHash);
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
            var result = await rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendMany, ct, args);

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
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} did not return a transaction id!");
                else
                    logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

                await PersistPaymentsAsync(balances, txId);

                NotifyPayoutSuccess(poolConfig.Id, balances, new[]
                {
                    txId
                }, null);
            }

            else
            {
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
            var txFailures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
            var successBalances = new Dictionary<Balance, string>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(amounts, parallelOptions, async (x, _ct) =>
            {
                var (address, amount) = x;

                await Guard(async () =>
                {
                    // use a common id for all log entries related to this transfer
                    var transferId = CorrelationIdGenerator.GetNextId();

                    logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending {FormatAmount(amount)} to {address}");

                    var result = await rpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendToAddress, ct, new object[]
                    {
                        address,
                        amount,
                    });

                    // check result
                    var txId = result.Response;

                    if(result.Error != null)
                        throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}");

                    if(string.IsNullOrEmpty(txId))
                        throw new Exception($"[{transferId}] {BitcoinCommands.SendToAddress} did not return a transaction id!");
                    else
                        logger.Info(() => $"[{LogCategory}] [{transferId}] Payment transaction id: {txId}");

                    successBalances.Add(new Balance
                    {
                        PoolId = poolConfig.Id,
                        Address = address,
                        Amount = amount,
                    }, txId);
                }, ex =>
                {
                    txFailures.Add(Tuple.Create(x, ex));
                });
            });

            if(successBalances.Any())
            {
                await PersistPaymentsAsync(successBalances);

                NotifyPayoutSuccess(poolConfig.Id, successBalances.Keys.ToArray(), successBalances.Values.ToArray(), null);
            }

            if(txFailures.Any())
            {
                var failureBalances = txFailures.Select(x=> new Balance { Amount = x.Item1.Value }).ToArray();
                var error = string.Join(", ", txFailures.Select(x => $"{x.Item1.Key} {FormatAmount(x.Item1.Value)}: {x.Item2.Message}"));

                logger.Error(()=> $"[{LogCategory}] Failed to transfer the following balances: {error}");

                NotifyPayoutFailure(poolConfig.Id, failureBalances, error, null);
            }
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler
}
