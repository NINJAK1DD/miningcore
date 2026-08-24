using System;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Autofac;
using AutoMapper;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using kaspaWalletd = Miningcore.Blockchain.Kaspa.KaspaWalletd;
using Kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

[CoinFamily(CoinFamily.Kaspa)]
public class KaspaPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public KaspaPayoutHandler(
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
    protected Kaspad.KaspadRPC.KaspadRPCClient rpc;
    protected kaspaWalletd.KaspaWalletdRPC.KaspaWalletdRPCClient walletRpc;
    protected string network;
    private KaspaPoolConfigExtra extraPoolConfig;
    private KaspaPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    private bool supportsMaxFee = false;

    internal sealed record PayoutTransactionIdentity(string CanonicalTransactionId,
        string[] TransactionIds);

    protected override string LogCategory => "Kaspa Payout Handler";
    
    #region IPayoutHandler
    
    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<KaspaPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<KaspaPaymentProcessingConfigExtra>();
        
        logger = LogUtil.GetPoolScopedLogger(typeof(KaspaPayoutHandler), pc);
        
        // extract standard daemon endpoints
        var daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();
        
        // extract wallet daemon endpoints
        var walletDaemonEndpoints = pc.Daemons
            .Where(x => x.Category?.ToLower() == KaspaConstants.WalletDaemonCategory)
            .ToArray();

        if(walletDaemonEndpoints.Length == 0)
            throw new PaymentException("Wallet-RPC daemon is not configured (Daemon configuration for kaspa-pools require an additional entry of category 'wallet' pointing to the wallet daemon)");

        rpc = KaspaClientFactory.CreateKaspadRPCClient(daemonEndpoints, extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName);
        walletRpc = KaspaClientFactory.CreateKaspaWalletdRPCClient(walletDaemonEndpoints, extraPoolConfig?.ProtobufWalletRpcServiceName ?? KaspaConstants.ProtobufWalletRpcServiceName);
        
        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);
        
        var request = new Kaspad.KaspadMessage();
        request.GetCurrentNetworkRequest = new Kaspad.GetCurrentNetworkRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> throw new PaymentException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}"));
        await foreach (var currentNetwork in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(currentNetwork.GetCurrentNetworkResponse.Error?.Message))
                throw new PaymentException($"Daemon reports: {currentNetwork.GetCurrentNetworkResponse.Error?.Message}");
            
            network = currentNetwork.GetCurrentNetworkResponse.CurrentNetwork;
            break;
        }
        await stream.RequestStream.CompleteAsync();

        var callGetVersion = walletRpc.GetVersionAsync(new kaspaWalletd.GetVersionRequest());
        var walletVersion = await Guard(() => callGetVersion.ResponseAsync,
            ex=> logger.Debug(ex));
        callGetVersion.Dispose();

        if(!string.IsNullOrEmpty(walletVersion?.Version))
        {
            logger.Info(() => $"[{LogCategory}] Wallet version: {walletVersion.Version}");

            if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.VersionEnablingMaxFee))
            {
                logger.Info(() => $"[{LogCategory}] Wallet daemon version which enables MaxFee: {extraPoolPaymentProcessingConfig.VersionEnablingMaxFee}");

                string walletVersionNumbersOnly = Regex.Replace(walletVersion.Version, "[^0-9.]", "");
                string[] walletVersionNumbers = walletVersionNumbersOnly.Split(".");

                string versionEnablingMaxFeeNumbersOnly = Regex.Replace(extraPoolPaymentProcessingConfig.VersionEnablingMaxFee, "[^0-9.]", "");
                string[] versionEnablingMaxFeeNumbers = versionEnablingMaxFeeNumbersOnly.Split(".");

                // update supports max fee
                if(walletVersionNumbers.Length >= 3 && versionEnablingMaxFeeNumbers.Length >= 3)
                    supportsMaxFee = ((Convert.ToUInt32(walletVersionNumbers[0]) > Convert.ToUInt32(versionEnablingMaxFeeNumbers[0])) || (Convert.ToUInt32(walletVersionNumbers[0]) == Convert.ToUInt32(versionEnablingMaxFeeNumbers[0]) && Convert.ToUInt32(walletVersionNumbers[1]) > Convert.ToUInt32(versionEnablingMaxFeeNumbers[1])) || (Convert.ToUInt32(walletVersionNumbers[0]) == Convert.ToUInt32(versionEnablingMaxFeeNumbers[0]) && Convert.ToUInt32(walletVersionNumbers[1]) == Convert.ToUInt32(versionEnablingMaxFeeNumbers[1]) && Convert.ToUInt32(walletVersionNumbers[2]) >= Convert.ToUInt32(versionEnablingMaxFeeNumbers[2])));
            }
        }
    }
    
    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if(blocks.Length == 0)
            return blocks;

        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();
        // KAS minimum confirmation can change over time so please always aknowledge all those different changes very wisely: https://github.com/kaspanet/rusty-kaspa/blob/master/wallet/core/src/utxo/settings.rs
        int minConfirmations = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? (network == "mainnet" ? 120 : 110);

        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();
    
            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];

                // There is a case scenario:
                // https://github.com/blackmennewstyle/miningcore/issues/191
                // Sadly miners can submit different solutions which will produce the exact same blockHash for the same block
                // We must handle that case carefully here, otherwise we will overpay our miners.
                // Only one of these blocks must will be confirmed, the others will all become Orphans
                uint totalDuplicateBlockBefore = await cf.Run(con => blockRepo.GetPoolDuplicateBlockBeforeCountByPoolHeightAndHashNoTypeAndStatusAsync(con, poolConfig.Id, Convert.ToInt64(block.BlockHeight), block.Hash, new[]
                {
                    BlockStatus.Confirmed,
                    BlockStatus.Orphaned,
                    BlockStatus.Pending
                }, block.Created));

                var request = new Kaspad.KaspadMessage();
                request.GetBlockRequest = new Kaspad.GetBlockRequestMessage
                {
                    Hash = block.Hash,
                    IncludeTransactions = true,
                };
                await Guard(() => stream.RequestStream.WriteAsync(request),
                    ex=> logger.Debug(ex));
                await foreach (var blockInfo in stream.ResponseStream.ReadAllAsync(ct))
                {
                    // We lost that battle
                    if(!string.IsNullOrEmpty(blockInfo.GetBlockResponse.Error?.Message))
                    {
                        result.Add(block);

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned because it's not the chain");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                    // multiple blocks with the exact same height & hash recorded in the database
                    else if(totalDuplicateBlockBefore > 0)
                    {
                        result.Add(block);

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] classified as orphaned because we already have in the database {totalDuplicateBlockBefore} block(s) with the same height and hash");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                    else
                    {
                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} uses a custom minimum confirmations calculation [{minConfirmations}]");

                        var requestConfirmations = new Kaspad.KaspadMessage();
                        requestConfirmations.GetBlocksRequest = new Kaspad.GetBlocksRequestMessage
                        {
                            LowHash = (string) block.Hash,
                            IncludeBlocks = false,
                            IncludeTransactions = false,
                        };
                        await Guard(() => stream.RequestStream.WriteAsync(requestConfirmations),
                            ex=> logger.Debug(ex));
                        await foreach (var responseConfirmations in stream.ResponseStream.ReadAllAsync(ct))
                        {
                            logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{responseConfirmations.GetBlocksResponse.BlockHashes.Count}]");

                            block.ConfirmationProgress = Math.Min(1.0d, (double) responseConfirmations.GetBlocksResponse.BlockHashes.Count / minConfirmations);
                            break;
                        }

                        result.Add(block);

                        messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                        
                        // matured and spendable?
                        if(block.ConfirmationProgress >= 1)
                        {
                            
                            // KASPA block reward calculation is a complete nightmare: https://wiki.kaspa.org/en/merging-and-rewards
                            decimal blockReward = 0.0m;
                            
                            var childrenProvideRewards = false;
                            
                            // First: We need the parse the children(s) related to the block reward, because in GhostDAG the child(s) reward(s) the parent
                            foreach(var childrenHash in blockInfo.GetBlockResponse.Block.VerboseData.ChildrenHashes)
                            {
                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} contains child: {childrenHash}");

                                var requestChildren = new Kaspad.KaspadMessage();
                                requestChildren.GetBlockRequest = new Kaspad.GetBlockRequestMessage
                                {
                                    Hash = childrenHash,
                                    IncludeTransactions = true,
                                };
                                await Guard(() => stream.RequestStream.WriteAsync(requestChildren),
                                    ex=> logger.Debug(ex));
                                await foreach (var responseChildren in stream.ResponseStream.ReadAllAsync(ct))
                                {
                                    // we only need the transaction(s) related to the block reward
                                    var childrenBlockRewardTransactions = responseChildren.GetBlockResponse.Block.Transactions
                                        .Where(x => x.Inputs.Count < 1)
                                        .ToList();
                                    
                                    if(childrenBlockRewardTransactions.Count > 0)
                                    {
                                        // We need to know if our initial blockHah is in the redMerges
                                        var mergeSetRedsHashess = responseChildren.GetBlockResponse.Block.VerboseData.MergeSetRedsHashes
                                            .Where(x => x.Contains((string) block.Hash))
                                            .ToList();

                                        // We need to know if our initial blockHah is in the blueMerges
                                        var mergeSetBluesHashes = responseChildren.GetBlockResponse.Block.VerboseData.MergeSetBluesHashes
                                            .Where(x => x.Contains((string) block.Hash))
                                            .ToList();
                                        
                                        if(mergeSetRedsHashess.Count > 0)
                                        {
                                            logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount(0.0m)}");
                                        }
                                        else if(mergeSetBluesHashes.Count > 0 && responseChildren.GetBlockResponse.Block.VerboseData.IsChainBlock)
                                        {
                                            var childrenPosition = responseChildren.GetBlockResponse.Block.VerboseData.MergeSetBluesHashes.IndexOf((string) block.Hash);
                                            
                                            // Are those rewards going to the pool wallet?
                                            if(childrenBlockRewardTransactions.First().Outputs[childrenPosition].VerboseData.ScriptPublicKeyAddress == poolConfig.Address)
                                            {
                                                childrenProvideRewards = true;

                                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount((decimal) (childrenBlockRewardTransactions.First().Outputs[childrenPosition].Amount / KaspaConstants.SmallestUnit))} => {coin.Symbol} address: {childrenBlockRewardTransactions.First().Outputs[childrenPosition].VerboseData.ScriptPublicKeyAddress} [{poolConfig.Address}]");
                                                blockReward += (decimal) (childrenBlockRewardTransactions.First().Outputs[childrenPosition].Amount / KaspaConstants.SmallestUnit);
                                            }
                                            else
                                                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount(0.0m)}");
                                            
                                        }
                                        else
                                            logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] provides {FormatAmount(0.0m)}");
                                    }
                                    else
                                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} - block child {responseChildren.GetBlockResponse.Block.Header.DaaScore} [{childrenHash}] does not contain transaction(s) related to the block reward, block maybe will not be unlocked :'(");

                                    break;
                                }
                            }
                            
                            // Hold on, we still have one more thing to check
                            if(blockInfo.GetBlockResponse.Block.VerboseData.IsChainBlock && childrenProvideRewards == false)
                            {
                                // we only need the transaction(s) related to the block reward
                                var blockRewardTransactions = blockInfo.GetBlockResponse.Block.Transactions
                                    .Where(x => x.Inputs.Count < 1)
                                    .ToList();
                                
                                if(blockRewardTransactions.Count > 0)
                                {
                                    // We only need the transactions for the pool wallet
                                    var amounts = blockRewardTransactions.First().Outputs
                                        .Where(x => x.VerboseData.ScriptPublicKeyAddress == poolConfig.Address)
                                        .ToList();

                                    if(amounts.Count > 0)
                                    {
                                        var totalAmount = amounts
                                            .Sum(x => (x.Amount / KaspaConstants.SmallestUnit));
                                        
                                        logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} contains: {FormatAmount(totalAmount)}");
                                        blockReward += (decimal) totalAmount;
                                    }
                                    else
                                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} coinbase transaction(s) provide(s) {FormatAmount(0.0m)}");
                                }
                                else
                                    logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} does not contain transaction(s) related to the block reward, block maybe will not be unlocked :'(");
                            }
                            
                            if(blockReward > 0)
                            {
                                block.Status = BlockStatus.Confirmed;
                                block.ConfirmationProgress = 1;

                                // reset block reward
                                block.Reward = blockReward;

                                logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            }
                            else
                            {
                                logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} does not receive any block reward :'(");
                                
                                block.Status = BlockStatus.Orphaned;
                                block.Reward = 0;

                                logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned because no reward has been found");

                                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            }
                        }
                    }
                    break;
                }
            }
        }
        await stream.RequestStream.CompleteAsync();

        return result.ToArray();
    }
    
    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        await TrackPayoutAsync(balances, () => PayoutTrackedAsync(balances, ct));
    }

    protected virtual async Task PayoutTrackedAsync(Balance[] balances,
        CancellationToken ct)
    {
        // build args
        var amounts = balances
            .Where(x => x.Amount > 0)
            .OrderBy(x => x.Updated)
            .ThenByDescending(x => x.Amount)
            .ToDictionary(x => x.Address, x => x.Amount);

        if(amounts.Count == 0)
            return;

        var balancesTotal = amounts.Sum(x => x.Value);
        
        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balancesTotal)} to {balances.Length} addresses");
        
        logger.Info(() => $"[{LogCategory}] Validating addresses...");
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        foreach(var pair in amounts)
        {
            logger.Debug(() => $"[{LogCategory}] Address {pair.Key} with amount [{FormatAmount(pair.Value)}]");
            var (kaspaAddressUtility, errorKaspaAddressUtility) = KaspaUtils.ValidateAddress(pair.Key, network, coin);

            if(errorKaspaAddressUtility != null)
                logger.Warn(()=> $"[{LogCategory}] Address {pair.Key} is not valid : {errorKaspaAddressUtility}");
        }
        
        var walletBalances = await GetPayoutWalletBalanceAsync(ct);
        
        var walletBalancePending = (decimal) (walletBalances?.Pending == null ? 0 : walletBalances?.Pending) / KaspaConstants.SmallestUnit;
        var walletBalanceAvailable = (decimal) (walletBalances?.Available == null ? 0 : walletBalances?.Available) / KaspaConstants.SmallestUnit;
        
        logger.Info(() => $"[{LogCategory}] Current wallet balance - Total: [{FormatAmount(walletBalancePending + walletBalanceAvailable)}] - Pending: [{FormatAmount(walletBalancePending)}] - Available: [{FormatAmount(walletBalanceAvailable)}]");

        // bail if balance does not satisfy payments
        if(walletBalanceAvailable < balancesTotal)
        {
            logger.Warn(() => $"[{LogCategory}] Wallet balance currently short of {FormatAmount(balancesTotal - walletBalanceAvailable)}. Will try again");
            return;
        }

        var txFailures = new List<Tuple<KeyValuePair<string, decimal>, Exception>>();
        var successBalances = new Dictionary<Balance, PayoutTransactionIdentity>();

        OperationCanceledException loopCancellation = null;

        try
        {
            // Payments on KASPA are a bit tricky, it does not have a strong multi-recipient method, the only way is to create unsigned transactions, signed them and then broadcast them, let's do this!
            foreach (var amount in amounts)
            {
                kaspaWalletd.CreateUnsignedTransactionsResponse unsignedTransaction;
                kaspaWalletd.SignResponse signedTransaction;

                // use a common id for all log entries related to this transfer
                var transferId = CorrelationIdGenerator.GetNextId();

                logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending {FormatAmount(amount.Value)} to {amount.Key}");

                logger.Info(()=> $"[{LogCategory}] [{transferId}] 1/3 Create an unsigned transaction");

                var createUnsignedTransactionsRequest = new kaspaWalletd.CreateUnsignedTransactionsRequest
                {
                    Address = amount.Key.ToLower(),
                    Amount = (ulong) (amount.Value * KaspaConstants.SmallestUnit),
                    UseExistingChangeAddress = false,
                    IsSendAll = false
                };

                if(supportsMaxFee)
                {
                    ulong maxFee = extraPoolPaymentProcessingConfig?.MaxFee ?? 20000;

                    logger.Info(()=> $"[{LogCategory}] Max fee: {maxFee} SOMPI");

                    createUnsignedTransactionsRequest.FeePolicy = new kaspaWalletd.FeePolicy
                    {
                        MaxFee = maxFee
                    };
                }

                try
                {
                    unsignedTransaction = await CreateUnsignedTransactionsAsync(
                        createUnsignedTransactionsRequest, ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested)
                {
                    throw;
                }
                catch(Exception ex)
                {
                    RecordPreparationFailure(amount, ex, txFailures);
                    continue;
                }

                logger.Debug(()=> $"[{LogCategory}] [{transferId}] {(unsignedTransaction?.UnsignedTransactions == null ? 0 : unsignedTransaction?.UnsignedTransactions.Count)} unsigned transaction(s) created");

                if(unsignedTransaction?.UnsignedTransactions.Count is not > 0)
                {
                    RecordPreparationFailure(amount, new PaymentException(
                        "Kaspa wallet returned no unsigned transactions"), txFailures);
                    continue;
                }

                // we have transactions to sign
                {
                    logger.Info(()=> $"[{LogCategory}] [{transferId}] 2/3 Sign {unsignedTransaction.UnsignedTransactions.Count} unsigned transaction(s)");

                    var signRequest = new kaspaWalletd.SignRequest
                    {
                        Password = extraPoolPaymentProcessingConfig?.WalletPassword ?? string.Empty
                    };
                    signRequest.UnsignedTransactions.Add(unsignedTransaction.UnsignedTransactions);

                    try
                    {
                        signedTransaction = await SignTransactionsAsync(signRequest, ct);
                    }
                    catch(OperationCanceledException) when(ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch(Exception ex)
                    {
                        RecordPreparationFailure(amount, ex, txFailures);
                        continue;
                    }

                    logger.Debug(()=> $"[{LogCategory}] [{transferId}] {(signedTransaction?.SignedTransactions == null ? 0 : signedTransaction?.SignedTransactions.Count)} signed transaction(s) created");

                    if(signedTransaction?.SignedTransactions.Count is not > 0)
                    {
                        RecordPreparationFailure(amount, new PaymentException(
                            "Kaspa wallet returned no signed transactions"), txFailures);
                        continue;
                    }

                    if(signedTransaction.SignedTransactions.Count !=
                        unsignedTransaction.UnsignedTransactions.Count)
                    {
                        RecordPreparationFailure(amount, new PaymentException(
                            $"Kaspa wallet returned {signedTransaction.SignedTransactions.Count} " +
                            $"signed transaction(s) for " +
                            $"{unsignedTransaction.UnsignedTransactions.Count} unsigned " +
                            "transaction(s)"), txFailures);
                        continue;
                    }

                    // we have transactions to broadcast
                    {
                        var broadcastRequest = new kaspaWalletd.BroadcastRequest();
                        kaspaWalletd.BroadcastResponse broadcastTransaction;

                        logger.Info(()=> $"[{LogCategory}] [{transferId}] 3/3 Broadcast {signedTransaction.SignedTransactions.Count} signed transaction(s)");

                        broadcastRequest.Transactions.Add(signedTransaction.SignedTransactions);
                        var submittedBalance = new Balance
                        {
                            PoolId = poolConfig.Id,
                            Address = amount.Key,
                            Amount = amount.Value,
                        };
                        BeforePayoutSubmission(submittedBalance, ct);
                        TrackPayoutSubmission(ct, submittedBalance);
                        try
                        {
                            broadcastTransaction = await BroadcastTransactionAsync(
                                broadcastRequest, ct);
                        }
                        catch(Exception ex)
                        {
                            WalletSubmissionOutcome.RethrowIfUnknown(ex,
                                "Kaspa wallet transaction broadcast");
                            logger.Warn(ex);
                            TrackPayoutFailure(new[] { submittedBalance }, ex.Message);
                            txFailures.Add(Tuple.Create(amount, ex));
                            continue;
                        }

                        logger.Debug(()=> $"[{LogCategory}] {(broadcastTransaction?.TxIDs == null ? 0 : broadcastTransaction?.TxIDs.Count)} transaction ID(s) returned");
                        var returnedIds = broadcastTransaction?.TxIDs?.ToArray() ??
                            Array.Empty<string>();
                        TrackReturnedPayoutTransactions(new[] { submittedBalance },
                            returnedIds);
                        var identity = ValidateBroadcastResponse(
                            signedTransaction.SignedTransactions.Count,
                            broadcastTransaction);

                        foreach(var txId in identity.TransactionIds)
                            logger.Info(() => $"[{LogCategory}] [{amount.Key} - {FormatAmount(amount.Value)}] Payment transaction id: {txId}");

                        TrackPayoutTransactions(new[] { submittedBalance },
                            identity.CanonicalTransactionId, identity.TransactionIds);
                        successBalances.Add(submittedBalance, identity);
                    }
                }
            }
        }
        catch(OperationCanceledException ex) when(ct.IsCancellationRequested)
        {
            // A later recipient observed shutdown before its broadcast began. Transactions
            // already returned by the wallet are conclusive and must be persisted first.
            loopCancellation = ex;
        }

        if(successBalances.Any())
        {
            var successfulPayouts = successBalances.ToArray();

            await PersistPaymentsAsync(successfulPayouts.ToDictionary(x => x.Key,
                x => x.Value.CanonicalTransactionId));

            NotifyPayoutSuccess(poolConfig.Id,
                successfulPayouts.Select(x => x.Key).ToArray(),
                successfulPayouts.SelectMany(x => x.Value.TransactionIds).ToArray(),
                null, recipientTransactionChains: successfulPayouts.Select(x =>
                    new PaymentRecipientTransactionChain
                    {
                        Address = x.Key.Address,
                        CanonicalTransactionId = x.Value.CanonicalTransactionId,
                        TransactionIds = x.Value.TransactionIds,
                    }).ToArray());
        }

        if(txFailures.Any())
        {
            var failureBalances = txFailures.Select(x => new Balance
            {
                PoolId = poolConfig.Id,
                Address = x.Item1.Key,
                Amount = x.Item1.Value,
            }).ToArray();
            var error = string.Join(", ", txFailures.Select(x => $"{x.Item1.Key} {FormatAmount(x.Item1.Value)}: {x.Item2.Message}"));

            logger.Error(()=> $"[{LogCategory}] Failed to transfer the following balances: {error}");

            NotifyPayoutFailure(poolConfig.Id, failureBalances, error, null);
        }

        if(loopCancellation != null)
            ExceptionDispatchInfo.Capture(loopCancellation).Throw();
    }

    protected virtual async Task<kaspaWalletd.GetBalanceResponse>
        GetPayoutWalletBalanceAsync(CancellationToken ct)
    {
        using var call = walletRpc.GetBalanceAsync(
            new kaspaWalletd.GetBalanceRequest(), cancellationToken: ct);
        return await Guard(() => call.ResponseAsync, ex =>
        {
            RethrowCancellation(ex, ct);
            logger.Debug(ex);
        });
    }

    protected virtual async Task<kaspaWalletd.CreateUnsignedTransactionsResponse>
        CreateUnsignedTransactionsAsync(
            kaspaWalletd.CreateUnsignedTransactionsRequest request,
            CancellationToken ct)
    {
        using var call = walletRpc.CreateUnsignedTransactionsAsync(request,
            cancellationToken: ct);
        return await call.ResponseAsync;
    }

    protected virtual async Task<kaspaWalletd.SignResponse> SignTransactionsAsync(
        kaspaWalletd.SignRequest request, CancellationToken ct)
    {
        using var call = walletRpc.SignAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    protected virtual async Task<kaspaWalletd.BroadcastResponse>
        BroadcastTransactionAsync(kaspaWalletd.BroadcastRequest request,
            CancellationToken ct)
    {
        using var call = walletRpc.BroadcastAsync(request, cancellationToken: ct);
        return await call.ResponseAsync;
    }

    protected virtual void BeforePayoutSubmission(Balance balance,
        CancellationToken ct)
    {
    }

    private static void RethrowCancellation(Exception ex, CancellationToken ct)
    {
        if(!ct.IsCancellationRequested)
            return;

        if(ex is OperationCanceledException)
            ExceptionDispatchInfo.Capture(ex).Throw();

        if(ex is RpcException { StatusCode: StatusCode.Cancelled })
            throw new OperationCanceledException(
                "Kaspa wallet operation was cancelled", ex, ct);
    }

    internal static PayoutTransactionIdentity ValidateBroadcastResponse(
        int signedTransactionCount, kaspaWalletd.BroadcastResponse response)
    {
        if(signedTransactionCount <= 0)
            throw new PayoutOutcomeUncertainException(
                "Kaspa wallet transaction broadcast had no submitted transactions");

        var transactionIds = response?.TxIDs?.ToArray() ?? Array.Empty<string>();
        if(transactionIds.Length != signedTransactionCount)
            throw new PayoutOutcomeUncertainException(
                $"Kaspa wallet transaction broadcast returned " +
                $"{transactionIds.Length} transaction id(s) for " +
                $"{signedTransactionCount} submitted transaction(s)");

        for(var i = 0; i < transactionIds.Length; i++)
            WalletSubmissionOutcome.RequireTransactionId(transactionIds[i],
                $"Kaspa wallet transaction broadcast result {i + 1}");

        if(transactionIds.Distinct(StringComparer.Ordinal).Count() !=
            transactionIds.Length)
            throw new PayoutOutcomeUncertainException(
                "Kaspa wallet transaction broadcast returned duplicate transaction ids");

        // Kaspa wallet auto-compounding appends the merge transaction after its prerequisite
        // split transactions. The final ordered identity is therefore recipient-facing and is
        // the canonical payment-history and idempotency identity.
        return new PayoutTransactionIdentity(transactionIds[^1], transactionIds);
    }

    protected void RecordPreparationFailure(KeyValuePair<string, decimal> amount,
        Exception ex,
        ICollection<Tuple<KeyValuePair<string, decimal>, Exception>> failures)
    {
        TrackPayoutFailure(new[]
        {
            new Balance
            {
                PoolId = poolConfig.Id,
                Address = amount.Key,
                Amount = amount.Value,
            },
        }, ex.Message);
        failures.Add(Tuple.Create(amount, ex));
    }

    public override double AdjustShareDifficulty(double difficulty)
    {
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();

        switch(coin.Symbol)
        {
            case "SPR":

                return difficulty * SpectreConstants.Pow2xDiff1TargetNumZero * (double) SpectreConstants.MinHash;
            default:

                return difficulty * KaspaConstants.Pow2xDiff1TargetNumZero * (double) KaspaConstants.MinHash;
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();

        switch(coin.Symbol)
        {
            case "SPR":

                return effort * SpectreConstants.Pow2xDiff1TargetNumZero * (double) SpectreConstants.MinHash;
            default:

                return effort * KaspaConstants.Pow2xDiff1TargetNumZero * (double) KaspaConstants.MinHash;
        }
    }
    
    #endregion // IPayoutHandler

    private class PaymentException : Exception
    {
        public PaymentException(string msg) : base(msg)
        {
        }
    }
}
