using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Blockchain.Warthog.DaemonRequests;
using Miningcore.Blockchain.Warthog.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rest;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Warthog;

[CoinFamily(CoinFamily.Warthog)]
public class WarthogPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public WarthogPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IHttpClientFactory httpClientFactory,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
        this.httpClientFactory = httpClientFactory;
    }

    protected readonly IComponentContext ctx;
    private IHttpClientFactory httpClientFactory;
    private SimpleRestClient restClient;
    private string network;
    private WarthogPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    private int minConfirmations;
    private decimal maximumTransactionFees;
    private ECPrivKey ellipticPrivateKey;
    private readonly IHashAlgorithm sha256S = new Sha256S();
    private object nonceGenLock = new();

    protected sealed record RecipientPayoutResult(Balance Balance, string TxHash,
        Exception Error);

    protected override string LogCategory => "Warthog Payout Handler";
    
    #region IPayoutHandler

    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<WarthogPaymentProcessingConfigExtra>();

        maximumTransactionFees = extraPoolPaymentProcessingConfig?.MaximumTransactionFees ?? WarthogConstants.MinimumTransactionFees;
    
        logger = LogUtil.GetPoolScopedLogger(typeof(WarthogPayoutHandler), pc);

        // configure standard daemon
        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        
        var daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        restClient = new SimpleRestClient(httpClientFactory, "http://" + daemonEndpoints.First().Host.ToString() + ":" + daemonEndpoints.First().Port.ToString());

        try
        {
            var response = await restClient.Get<WarthogBlockTemplate>(WarthogCommands.GetBlockTemplate.Replace(WarthogCommands.DataLabel, poolConfig.Address), ct);
            if(response?.Error != null)
                throw new Exception($"Pool address '{poolConfig.Address}': {response.Error} (Code {response?.Code})");

            network = response.Data.Testnet ? "testnet" : "mainnet";
        }

        catch(Exception e)
        {
            logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.DaemonName} - {WarthogCommands.GetBlockTemplate}' daemon does not seem to be running...");
            throw new Exception($"'{WarthogCommands.DaemonName}' returned error: {e}");
        }

        if(string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPrivateKey))
            throw new Exception("WalletPrivateKey is mandatory for signing and sending transactions");
        else
        {
            try
            {
                var responsePoolAddressWalletPrivateKey = await restClient.Get<WarthogWalletResponse>(WarthogCommands.GetWallet.Replace(WarthogCommands.DataLabel, extraPoolPaymentProcessingConfig?.WalletPrivateKey), ct);
                if(responsePoolAddressWalletPrivateKey?.Error != null)
                    throw new Exception($"Pool address private key '{extraPoolPaymentProcessingConfig?.WalletPrivateKey}': {responsePoolAddressWalletPrivateKey.Error} (Code {responsePoolAddressWalletPrivateKey?.Code})");

                if(responsePoolAddressWalletPrivateKey.Data.Address != poolConfig.Address)
                    throw new Exception($"Pool address private key '{extraPoolPaymentProcessingConfig?.WalletPrivateKey}' [{responsePoolAddressWalletPrivateKey.Data.Address}] does not match pool address: {poolConfig.Address}");
            }

            catch(Exception e)
            {
                logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.DaemonName} - {WarthogCommands.GetWallet}' daemon does not seem to be running...");
                throw new Exception($"'{WarthogCommands.DaemonName}' returned error: {e}");
            }

            ellipticPrivateKey = Context.Instance.CreateECPrivKey(extraPoolPaymentProcessingConfig.WalletPrivateKey.HexToByteArray());
        }

        minConfirmations = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? (network == "mainnet" ? 120 : 110);
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if(blocks.Length == 0)
            return blocks;

        GetChainInfoResponse chainInfo;
        try
        {
            chainInfo = await restClient.Get<GetChainInfoResponse>(WarthogCommands.GetChainInfo, ct);
            if(chainInfo?.Error != null)
            {
                logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.GetChainInfo}': {chainInfo.Error} (Code {chainInfo?.Code})");
                return blocks;
            }
        }

        catch(Exception)
        {
            logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.DaemonName} - {WarthogCommands.GetChainInfo}' daemon does not seem to be running...");
            return blocks;
        }

        var coin = poolConfig.Template.As<WarthogCoinTemplate>();
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

            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];
                
                WarthogBlock response;
                try
                {
                    response = await restClient.Get<WarthogBlock>(WarthogCommands.GetBlockByHeight.Replace(WarthogCommands.DataLabel, block.BlockHeight.ToString()), ct);
                    if(response?.Error != null)
                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight}: {response.Error} (Code {response?.Code})");
                }

                catch(Exception e)
                {
                    logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.DaemonName} - {WarthogCommands.GetBlockByHeight}' daemon does not seem to be running...");
                    throw new Exception($"'{WarthogCommands.DaemonName}' returned error: {e}");
                }

                // We lost that battle
                if(response?.Data.Header.Hash != block.Hash)
                {
                    result.Add(block);

                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned because {(response?.Error != null ? "it's not on chain" : $"it has a different hash on chain: {response.Data.Header.Hash}")}");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }
                else
                {
                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} uses a custom minimum confirmations calculation [{minConfirmations}]");

                    block.ConfirmationProgress = Math.Min(1.0d, (double) (chainInfo.Data.Height - block.BlockHeight) / minConfirmations);

                    result.Add(block);

                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                    // matured and spendable?
                    if(block.ConfirmationProgress >= 1)
                    {
                        block.ConfirmationProgress = 1;

                        logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} contains {response.Data.Body.BlockReward.Length} block reward(s)");

                        // reset block reward
                        block.Reward = 0;

                        // we only need the block reward(s) related to our pool address
                        var blockRewards = response.Data.Body.BlockReward
                            .Where(x => x.ToAddress.Contains(poolConfig.Address))
                            .ToList();

                        foreach (var blockReward in blockRewards)
                        {
                            block.Reward += (decimal) blockReward.Amount / WarthogConstants.SmallestUnit;
                        }

                        // security
                        if (block.Reward > 0)
                        {
                            block.Status = BlockStatus.Confirmed;

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                        }
                        else
                        {
                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                        }
                        
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
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
            .ToDictionary(x => x.Address, x => x.Amount);

        if(amounts.Count == 0)
            return;

        var balancesTotal = amounts.Sum(x => x.Value);
        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balancesTotal)} to {balances.Length} addresses");

        logger.Info(() => $"[{LogCategory}] Validating addresses...");
        foreach(var pair in amounts)
        {
            logger.Debug(() => $"[{LogCategory}] Address {pair.Key} with amount [{FormatAmount(pair.Value)}]");
            try
            {
                var responseAddress = await GetPayoutAddressTemplateAsync(pair.Key, ct);
                if(responseAddress?.Error != null)
                    logger.Warn(()=> $"[{LogCategory}] Address {pair.Key} is not valid: {responseAddress.Error} (Code {responseAddress?.Code})");
            }

            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                throw;
            }
            catch(Exception ex)
            {
                logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.DaemonName} - {WarthogCommands.GetBlockTemplate}' daemon does not seem to be running...");
                throw new Exception($"'{WarthogCommands.DaemonName}' returned error: {ex.Message}", ex);
            }
        }
        
        WarthogBalance responseBalance;
        try
        {
            responseBalance = await GetPayoutWalletBalanceAsync(ct);
            if(responseBalance?.Error != null)
                logger.Warn(()=> $"[{LogCategory}] '{WarthogCommands.GetBalance}': {responseBalance.Error} (Code {responseBalance?.Code})");
        }

        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.Warn(() => $"[{LogCategory}] '{WarthogCommands.DaemonName} - {WarthogCommands.GetBalance}' daemon does not seem to be running...");
            throw new Exception($"'{WarthogCommands.DaemonName}' returned error: {ex.Message}", ex);
        }

        var walletBalance = (decimal) (responseBalance?.Data.Balance == null ? 0 : responseBalance?.Data.Balance) / WarthogConstants.SmallestUnit;
    
        logger.Info(() => $"[{LogCategory}] Current wallet balance - Total: [{FormatAmount(walletBalance)}]");

        // bail if balance does not satisfy payments
        if(walletBalance < balancesTotal)
        {
            logger.Warn(() => $"[{LogCategory}] Wallet balance currently short of {FormatAmount(balancesTotal - walletBalance)}. Will try again");
            return;
        }

        if(extraPoolPaymentProcessingConfig?.KeepTransactionFees == true)
            logger.Debug(() => $"[{LogCategory}] Pool does not pay the transaction fee, so each address will have its payout deducted with [{FormatAmount(maximumTransactionFees / WarthogConstants.SmallestUnit)}]");

        var txFailures = new ConcurrentBag<Tuple<KeyValuePair<string, decimal>, Exception>>();
        var successBalances = new ConcurrentDictionary<Balance, string>();

        Random randomNonceId = new Random();
        List<uint> usedNonceId = new List<uint>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = PayoutMaxDegreeOfParallelism,
            CancellationToken = ct
        };

        OperationCanceledException loopCancellation = null;

        try
        {
            await Parallel.ForEachAsync(amounts, parallelOptions, async (x, _ct) =>
            {
                var (address, amount) = x;

                uint nonceId = (uint) randomNonceId.NextInt64((long)uint.MinValue, (long)uint.MaxValue);

                lock(nonceGenLock)
                {
                    bool IsSafeToContinue = false;
                    while(!IsSafeToContinue)
                    {
                        if(!(usedNonceId.Contains(nonceId)))
                        {
                            logger.Debug(()=> $"[{LogCategory}] Transaction nonceId: [{nonceId}]");

                            usedNonceId.Add(nonceId);
                            IsSafeToContinue = true;
                        }
                        else
                            nonceId = (uint) randomNonceId.NextInt64((long)uint.MinValue, (long)uint.MaxValue);
                    }
                }

                logger.Info(()=> $"[{LogCategory}] [{nonceId}] Sending {FormatAmount(amount)} to {address}");

                var result = await PayoutRecipientAsync(address, amount, nonceId, _ct);
                if(result.Error != null)
                    txFailures.Add(Tuple.Create(x, result.Error));
                else
                    successBalances.TryAdd(result.Balance, result.TxHash);
            });
        }
        catch(OperationCanceledException ex) when(ct.IsCancellationRequested)
        {
            // Parallel.ForEachAsync has drained active delegates. Persist transaction identities
            // returned before a later recipient observed shutdown, then propagate cancellation.
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

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler

    protected async Task<RecipientPayoutResult> PayoutRecipientAsync(string address,
        decimal amount, uint nonceId, CancellationToken ct)
    {
        var submittedBalance = new Balance
        {
            PoolId = poolConfig.Id,
            Address = address,
            Amount = amount,
        };

        WarthogSendTransactionRequest request;

        try
        {
            request = await PreparePayoutTransactionAsync(address, amount, nonceId, ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            // Chain, fee and signing work happens before the wallet submission boundary. Its
            // failures are conclusive preparation failures and never require reconciliation.
            TrackPayoutFailure(new[] { submittedBalance }, ex.Message);
            return new RecipientPayoutResult(submittedBalance, null, ex);
        }

        // Keep this outside the submission catch: an already-cancelled token must remain an
        // ordinary shutdown rather than being reclassified as an ambiguous POST.
        BeforePayoutSubmission(submittedBalance, ct);
        TrackPayoutSubmission(ct, submittedBalance);

        try
        {
            var response = await SendPayoutTransactionAsync(request, ct);
            if(response?.Error != null)
                throw new Exception($"[{nonceId}] {WarthogCommands.SendTransaction} returned error: {response.Error} (Code {response?.Code})");

            var txHash = WalletSubmissionOutcome.RequireTransactionId(
                response?.Data?.TxHash, WarthogCommands.SendTransaction);
            logger.Info(() => $"[{LogCategory}] [{nonceId}] Payment transaction id: {txHash}");

            TrackPayoutTransaction(new[] { submittedBalance }, txHash);
            return new RecipientPayoutResult(submittedBalance, txHash, null);
        }
        catch(Exception ex)
        {
            if(ex is HttpRequestException { StatusCode: { } statusCode } &&
                IsConclusiveHttpRejection(statusCode))
            {
                TrackPayoutFailure(new[] { submittedBalance }, ex.Message);
                return new RecipientPayoutResult(submittedBalance, null, ex);
            }

            WalletSubmissionOutcome.RethrowIfUnknown(ex,
                WarthogCommands.SendTransaction);
            TrackPayoutFailure(new[] { submittedBalance }, ex.Message);
            return new RecipientPayoutResult(submittedBalance, null, ex);
        }
    }

    protected virtual Task<WarthogBlockTemplate> GetPayoutAddressTemplateAsync(
        string address, CancellationToken ct) =>
        restClient.Get<WarthogBlockTemplate>(WarthogCommands.GetBlockTemplate.Replace(
            WarthogCommands.DataLabel, address), ct);

    protected virtual Task<WarthogBalance> GetPayoutWalletBalanceAsync(
        CancellationToken ct) =>
        restClient.Get<WarthogBalance>(WarthogCommands.GetBalance.Replace(
            WarthogCommands.DataLabel, poolConfig.Address), ct);

    protected virtual void BeforePayoutSubmission(Balance balance,
        CancellationToken ct)
    {
    }

    protected virtual int PayoutMaxDegreeOfParallelism =>
        extraPoolPaymentProcessingConfig?.MaxDegreeOfParallelPayouts ?? 2;

    protected virtual async Task<WarthogSendTransactionRequest>
        PreparePayoutTransactionAsync(string address, decimal amount, uint nonceId,
            CancellationToken ct)
    {
        // WART payment is quite complex: https://www.warthog.network/docs/developers/integrations/wallet-integration/
        var chainInfo = await restClient.Get<GetChainInfoResponse>(
            WarthogCommands.GetChainInfo, ct);
        if(chainInfo?.Error != null)
            throw new Exception($"'{WarthogCommands.GetChainInfo}': {chainInfo.Error} (Code {chainInfo?.Code})");

        var feeE8Encoded = await restClient.Get<WarthogFeeE8EncodedResponse>(
            WarthogCommands.GetFeeE8Encoded.Replace(WarthogCommands.DataLabel,
                maximumTransactionFees.ToString()), ct);
        if(feeE8Encoded?.Error != null)
            throw new Exception($"'{WarthogCommands.GetFeeE8Encoded}': {feeE8Encoded.Error} (Code {feeE8Encoded?.Code})");

        var amountE8 = (ulong) Math.Floor(((extraPoolPaymentProcessingConfig?.KeepTransactionFees == false) ? amount : (amount > (maximumTransactionFees / WarthogConstants.SmallestUnit) ? amount - (maximumTransactionFees / WarthogConstants.SmallestUnit) : amount)) * WarthogConstants.SmallestUnit);
        var pinHashBytes = chainInfo.Data.PinHash.HexToByteArray();
        var pinHeightNonceIdFeeBytes = SerializePinHeightNonceIdFee(
            chainInfo.Data.PinHeight, nonceId, feeE8Encoded.Data.Rounded);
        var toAddressBytes = address.HexToByteArray()
            .Take(WarthogConstants.ToAddressOffset).ToArray();
        var amountBytes = SerializeAmount(amountE8);
        var signatureBytes = SerializeSignature(pinHashBytes,
            pinHeightNonceIdFeeBytes, toAddressBytes, amountBytes);
        byte[] signatureHashBytes = new byte[32];
        sha256S.Digest(signatureBytes, (Span<byte>) signatureHashBytes);

        var signedECDSA = ellipticPrivateKey.TrySignECDSA(signatureHashBytes, null,
            out var recid, out var signatureECDSA);
        if(!signedECDSA || signatureECDSA == null)
            throw new Exception("SignECDSA failed (bug in C# secp256k1)");

        var fullSignatureBytes = SerializeFullSignature(signatureECDSA.r.ToBytes(),
            signatureECDSA.s.ToBytes(), (byte) recid);

        return new WarthogSendTransactionRequest
        {
            PinHeight = chainInfo.Data.PinHeight,
            NonceId = nonceId,
            ToAddress = address,
            Amount = amountE8,
            Fee = feeE8Encoded.Data.Rounded,
            Signature = fullSignatureBytes.ToHexString()
        };
    }

    protected virtual Task<WarthogSendTransactionResponse> SendPayoutTransactionAsync(
        WarthogSendTransactionRequest request, CancellationToken ct) =>
        restClient.Post<WarthogSendTransactionResponse>(
            WarthogCommands.SendTransaction, request, ct);

    // Restrict conclusive classification to client/request statuses that prove rejection.
    // Timeouts, throttling, conflicts and all server-side statuses remain fail-closed.
    private static bool IsConclusiveHttpRejection(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity;

    private byte[] SerializePinHeightNonceIdFee(uint pinHeight, uint nonceId, ulong feeE8Encoded)
    {
        using(var stream = new MemoryStream(WarthogConstants.PinHeightNonceIdFeeByteSize))
        {
            var bw = new BinaryWriter(stream);

            bw.Write((BitConverter.IsLittleEndian ? BitConverter.GetBytes(pinHeight).ReverseInPlace() : BitConverter.GetBytes(pinHeight))); // wart-node expects a big endian format.
            bw.Write((BitConverter.IsLittleEndian ? BitConverter.GetBytes(nonceId).ReverseInPlace() : BitConverter.GetBytes(nonceId))); // wart-node expects a big endian format.
            bw.Write(WarthogConstants.Zero);
            bw.Write(WarthogConstants.Zero);
            bw.Write(WarthogConstants.Zero);
            bw.Write((BitConverter.IsLittleEndian ? BitConverter.GetBytes(feeE8Encoded).ReverseInPlace() : BitConverter.GetBytes(feeE8Encoded))); // wart-node expects a big endian format.

            return stream.ToArray();
        }
    }

    private byte[] SerializeAmount(ulong amount)
    {
        using(var stream = new MemoryStream(WarthogConstants.AmountByteSize))
        {
            var bw = new BinaryWriter(stream);

            bw.Write((BitConverter.IsLittleEndian ? BitConverter.GetBytes(amount).ReverseInPlace() : BitConverter.GetBytes(amount))); // wart-node expects a big endian format.

            return stream.ToArray();
        }
    }

    private byte[] SerializeSignature(byte[] pinHashBytes, byte[] pinHeightNonceIdFeeBytes, byte[] toAddressBytes, byte[] amountBytes)
    {
        using(var stream = new MemoryStream())
        {
            stream.Write(pinHashBytes);
            stream.Write(pinHeightNonceIdFeeBytes);
            stream.Write(toAddressBytes);
            stream.Write(amountBytes);

            return stream.ToArray();
        }
    }

    private byte[] SerializeFullSignature(byte[] rBytes, byte[] sBytes, byte recid)
    {
        using(var stream = new MemoryStream(WarthogConstants.FullSignatureByteSize))
        {
            var bw = new BinaryWriter(stream);

            bw.Write(rBytes);
            bw.Write(sBytes);
            bw.Write(recid); // wart-node expects a big endian format.

            return stream.ToArray();
        }
    }
}
