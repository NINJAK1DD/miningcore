using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Handshake;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Handshake.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
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
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Handshake;

[CoinFamily(CoinFamily.Handshake)]
public class HandshakePayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public HandshakePayoutHandler(
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
    protected RpcClient rpcWallet;
    protected BitcoinPoolConfigExtra extraPoolConfig;
    protected BitcoinDaemonEndpointConfigExtra extraPoolEndpointConfig;
    protected HandshakePoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    private int payoutDecimalPlaces;
    private CoinTemplate coin;
    private int minConfirmations;

    protected override string LogCategory => "Handshake Payout Handler";

    #region IPayoutHandler

    public virtual Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolEndpointConfig = pc.Extra.SafeExtensionDataAs<BitcoinDaemonEndpointConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<HandshakePoolPaymentProcessingConfigExtra>();

        coin = poolConfig.Template.As<CoinTemplate>();
        if(coin is BitcoinTemplate bitcoinTemplate)
        {
            minConfirmations = extraPoolConfig?.MinimumConfirmations ??
                bitcoinTemplate.CoinbaseMinConfimations ??
                HandshakeConstants.CoinbaseMinConfimations;
            payoutDecimalPlaces = bitcoinTemplate.PayoutDecimalPlaces ?? 6;
        }
        else
            minConfirmations = extraPoolConfig?.MinimumConfirmations ??
                HandshakeConstants.CoinbaseMinConfimations;

        logger = LogUtil.GetPoolScopedLogger(typeof(HandshakePayoutHandler), pc);

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpcClient = new RpcClient(pc.Daemons.First(), jsonSerializerSettings, messageBus, pc.Id);

        // extract wallet daemon endpoints
        var walletDaemonEndpoints = pc.Daemons
            .Where(x => x.Category?.ToLower() == HandshakeConstants.WalletDaemonCategory)
            .ToArray();

        rpcWallet = new RpcClient(walletDaemonEndpoints.First(), jsonSerializerSettings, messageBus, pc.Id);

        return Task.CompletedTask;
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        var walletInfo = await rpcWallet.ExecuteAsync<WalletInfo>(logger, HandshakeWalletCommands.GetWalletInfo, ct);

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
            var batch = page.Select(block => new RpcRequest(HandshakeWalletCommands.GetTransaction,
                new[] { block.TransactionConfirmationData })).ToArray();

            // execute batch
            var results = await rpcWallet.ExecuteBatchAsync(logger, ct, batch);

            for(var j = 0; j < results.Length; j++)
            {
                var cmdResult = results[j];

                var transactionInfo = cmdResult.Response?.ToObject<HandshakeTransaction>();
                var block = page[j];

                // check error
                if(cmdResult.Error != null)
                {
                    // Code -5 interpreted as "orphaned"
                    if(cmdResult.Error.Code == -5)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to daemon error {cmdResult.Error.Code}");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }

                    else
                        logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {page[j].TransactionConfirmationData}");
                }

                // missing transaction details are interpreted as "orphaned"
                else if(transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to missing tx details");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }

                else
                {
                    var blockConfirmations = ((walletInfo.Response?.Height - block.BlockHeight) / (double) minConfirmations);

                    switch(transactionInfo.Details[0].Category)
                    {
                        case "receive":
                            // matured and spendable coinbase transaction
                            if(blockConfirmations >= 1)
                            {
                                block.Status = BlockStatus.Confirmed;
                                block.ConfirmationProgress = 1;
                                block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                                result.Add(block);

                                logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                                messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                            }
                            else
                            {
                                // immature, just update progress
                                block.ConfirmationProgress = Math.Min(1.0d, (double) blockConfirmations);
                                block.Reward = transactionInfo.Amount;  // update actual block-reward from coinbase-tx
                                result.Add(block);

                                messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                            }

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
            .ToDictionary(x => x.Address, x => Math.Round(x.Amount, payoutDecimalPlaces));

        if(amounts.Count == 0)
            return;

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        object[] args;

        var identifier = !string.IsNullOrEmpty(clusterConfig.PaymentProcessing?.CoinbaseString) ?
            clusterConfig.PaymentProcessing.CoinbaseString.Trim() : "Miningcore";

        var comment = $"{identifier} Payment";
        var walletAccount = extraPoolPaymentProcessingConfig?.WalletAccount ?? HandshakeConstants.WalletDefaultAccount;
        logger.Info(() => $"[{LogCategory}] Using wallet account: {walletAccount}");

        if(!(extraPoolConfig?.HasBrokenSendMany == true || poolConfig.Template is BitcoinTemplate { HasBrokenSendMany: true }))
        {
            if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
            {
                args = new object[]
                {
                    walletAccount,
                    amounts, // addresses and associated amounts
                    1, // only spend funds covered by this many confirmations
                    comment, // tx comment
                    true, // distribute transaction fee equally over all recipients
                };
            }

            else
            {
                args = new object[]
                {
                    walletAccount,
                    amounts, // addresses and associated amounts
                };
            }

            var didUnlockWallet = false;
            var shouldRelockWallet = false;
            try
            {
                // send command
                tryTransfer:
                var walletInfo = await GetWalletInfoAsync(ct);
                ct.ThrowIfCancellationRequested();

                var walletName = extraPoolPaymentProcessingConfig?.WalletName ??
                    HandshakeConstants.WalletDefaultName;

                if(walletInfo?.Error != null || walletInfo?.Response == null)
                {
                    var error = FormatPreflightError(
                        HandshakeWalletCommands.GetWalletInfo, walletInfo?.Error,
                        walletInfo == null || walletInfo.Response == null);
                    logger.Error(() => $"[{LogCategory}] {error}");
                    NotifyPayoutFailure(poolConfig.Id, balances, error, null);
                    return;
                }

                logger.Debug(() => $"[{LogCategory}] Current wallet: " +
                    $"{walletInfo.Response?.WalletId} [{walletName}]");
                if(walletInfo.Response?.WalletId != walletName)
                {
                    var selection = await SelectWalletAsync(walletName, ct);
                    ct.ThrowIfCancellationRequested();

                    if(selection == null || selection.Error != null)
                    {
                        var error = FormatPreflightError(
                            HandshakeWalletCommands.SelectWallet, selection?.Error,
                            selection == null);
                        logger.Error(() => $"[{LogCategory}] {error}");
                        NotifyPayoutFailure(poolConfig.Id, balances, error, null);
                        return;
                    }
                }

                TrackPayoutSubmission(ct, balances);
                var result = await SendManyAsync(args, ct);

                WalletSubmissionOutcome.ThrowIfUnknown(result.Error,
                    HandshakeWalletCommands.SendMany);

                if(result.Error == null)
                {
                    var txId = WalletSubmissionOutcome.RequireTransactionId(
                        result.Response, HandshakeWalletCommands.SendMany);
                    TrackPayoutTransaction(balances, txId);

                    logger.Info(() =>
                        $"[{LogCategory}] Payment transaction id: {txId}");

                    // Persist the already-broadcast transaction before wallet-security cleanup.
                    await PersistPaymentsAsync(balances, txId);

                    NotifyPayoutSuccess(poolConfig.Id, balances, new[] { txId }, null);
                }

                else
                {
                    if(result.Error.Code ==
                        (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED &&
                        !didUnlockWallet)
                    {
                        TrackPayoutSubmissionNotStarted(balances);

                        if(!string.IsNullOrEmpty(
                            extraPoolPaymentProcessingConfig?.WalletPassword))
                        {
                            logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                            RpcResponse<JToken> unlockResult;

                            try
                            {
                                unlockResult = await UnlockWalletAsync(ct);
                            }
                            catch(OperationCanceledException) when(ct.IsCancellationRequested)
                            {
                                // The daemon may have processed walletpassphrase before the
                                // cancelled response was observed. Relock conservatively.
                                shouldRelockWallet = true;
                                throw;
                            }

                            shouldRelockWallet = unlockResult == null ||
                                unlockResult.Error == null ||
                                WalletSubmissionOutcome.IsUnknown(unlockResult.Error);
                            ct.ThrowIfCancellationRequested();

                            if(unlockResult?.Error == null && unlockResult != null)
                            {
                                didUnlockWallet = true;
                                goto tryTransfer;
                            }

                            var error = FormatPreflightError(
                                HandshakeWalletCommands.WalletPassPhrase,
                                unlockResult?.Error, unlockResult == null);
                            logger.Error(() => $"[{LogCategory}] {error}");
                            NotifyPayoutFailure(poolConfig.Id, balances, error, null);
                        }

                        else
                        {
                            const string error = "Wallet is locked but walletPassword " +
                                "was not configured. Unable to send funds.";
                            logger.Error(() => $"[{LogCategory}] {error}");
                            NotifyPayoutFailure(poolConfig.Id, balances, error, null);
                        }
                    }

                    else
                    {
                        var error = $"{HandshakeWalletCommands.SendMany} returned " +
                            $"error: {result.Error.Message} code {result.Error.Code}";
                        logger.Error(() => $"[{LogCategory}] {error}");

                        NotifyPayoutFailure(poolConfig.Id, balances, error, null);
                    }
                }
            }
            finally
            {
                if(shouldRelockWallet)
                    await RelockPayoutWalletSafelyAsync(LockWallet);
            }
        }

        else
        {
            var txFailures = new ConcurrentBag<Tuple<KeyValuePair<string, decimal>, Exception>>();
            var successBalances = new ConcurrentDictionary<Balance, string>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = BrokenSendManyMaxDegreeOfParallelism,
                CancellationToken = ct
            };

            OperationCanceledException loopCancellation = null;

            try
            {
                await Parallel.ForEachAsync(amounts, parallelOptions, async (x, _ct) =>
                {
                    var (address, amount) = x;
                    var submittedBalance = new Balance
                    {
                        PoolId = poolConfig.Id,
                        Address = address,
                        Amount = amount,
                    };

                    BeforePayoutSubmission(submittedBalance, _ct);
                    // Keep the cancellation boundary outside Guard. Its error callback is
                    // reserved for exceptions raised after the broadcast-capable call begins.
                    TrackPayoutSubmission(_ct, submittedBalance);

                    await Guard(async () =>
                    {
                        // use a common id for all log entries related to this transfer
                        var transferId = CorrelationIdGenerator.GetNextId();

                        logger.Info(()=> $"[{LogCategory}] [{transferId}] Sending {FormatAmount(amount)} to {address}");

                        var result = await SendToAddressAsync(new object[]
                        {
                            address,
                            amount,
                        }, _ct);

                        // check result
                        var txId = result.Response;

                        WalletSubmissionOutcome.ThrowIfUnknown(result.Error,
                            HandshakeWalletCommands.SendToAddress);

                        if(result.Error != null)
                            throw new Exception($"[{transferId}] {HandshakeWalletCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}");

                        txId = WalletSubmissionOutcome.RequireTransactionId(txId,
                            HandshakeWalletCommands.SendToAddress);
                        logger.Info(() => $"[{LogCategory}] [{transferId}] Payment transaction id: {txId}");

                        TrackPayoutTransaction(new[] { submittedBalance }, txId);
                        successBalances.TryAdd(submittedBalance, txId);
                    }, ex =>
                    {
                        // Unknown outcomes, including duplicate transaction identities, must
                        // abort the batch before any successful subset is persisted.
                        WalletSubmissionOutcome.RethrowIfUnknown(ex,
                            HandshakeWalletCommands.SendToAddress);
                        TrackPayoutFailure(new[]
                        {
                            new Balance
                            {
                                PoolId = poolConfig.Id,
                                Address = x.Key,
                                Amount = x.Value,
                            },
                        }, ex.Message);
                        txFailures.Add(Tuple.Create(x, ex));
                    });
                });
            }
            catch(OperationCanceledException ex) when(ct.IsCancellationRequested)
            {
                // RpcClient converts cancellation during sendtoaddress into transport error
                // -500. A direct cancellation here occurred before a later submission began.
                loopCancellation = ex;
            }

            if(successBalances.Any())
            {
                await PersistPaymentsAsync(successBalances.ToDictionary(x => x.Key, x => x.Value));

                NotifyPayoutSuccess(poolConfig.Id, successBalances.Keys.ToArray(), successBalances.Values.ToArray(), null);
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
    }

    protected virtual Task<RpcResponse<WalletInfo>> GetWalletInfoAsync(
        CancellationToken ct) =>
        rpcWallet.ExecuteAsync<WalletInfo>(logger,
            HandshakeWalletCommands.GetWalletInfo, ct);

    protected virtual Task<RpcResponse<JToken>> SelectWalletAsync(string walletName,
        CancellationToken ct) =>
        rpcWallet.ExecuteAsync<JToken>(logger, HandshakeWalletCommands.SelectWallet,
            ct, new[] { walletName });

    protected virtual Task<RpcResponse<string>> SendManyAsync(object[] args,
        CancellationToken ct) =>
        rpcWallet.ExecuteAsync<string>(logger, HandshakeWalletCommands.SendMany,
            ct, args);

    protected virtual Task<RpcResponse<JToken>> UnlockWalletAsync(
        CancellationToken ct) =>
        rpcWallet.ExecuteAsync<JToken>(logger,
            HandshakeWalletCommands.WalletPassPhrase, ct, new object[]
            {
                extraPoolPaymentProcessingConfig.WalletPassword,
                5, // unlock for N seconds
            });

    private static string FormatPreflightError(string operation,
        JsonRpcError error, bool emptyResponse)
    {
        if(error != null)
            return $"{operation} failed: {error.Message} code {error.Code}";

        return emptyResponse
            ? $"{operation} failed: empty response"
            : $"{operation} failed";
    }

    protected virtual async Task LockWallet(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Locking wallet");

        var response = await rpcWallet.ExecuteAsync<JToken>(logger,
            HandshakeWalletCommands.WalletLock, ct);
        if(response.Error != null)
            throw new InvalidOperationException(
                $"{HandshakeWalletCommands.WalletLock} returned error: " +
                $"{response.Error.Message} code {response.Error.Code}");

        logger.Info(() => $"[{LogCategory}] Wallet locked");
    }

    protected virtual Task<RpcResponse<string>> SendToAddressAsync(object[] args,
        CancellationToken ct) =>
        rpcWallet.ExecuteAsync<string>(logger, HandshakeWalletCommands.SendToAddress,
            ct, args);

    protected virtual void BeforePayoutSubmission(Balance balance,
        CancellationToken ct)
    {
    }

    protected virtual int BrokenSendManyMaxDegreeOfParallelism => 8;

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler
}
