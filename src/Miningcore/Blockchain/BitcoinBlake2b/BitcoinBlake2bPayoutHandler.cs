using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using BlockStatus = Miningcore.Persistence.Model.BlockStatus;

namespace Miningcore.Blockchain.BitcoinBlake2b;

[CoinFamily(CoinFamily.BitcoinBlake2b)]
public class BitcoinBlake2bPayoutHandler : BitcoinPayoutHandler
{
    private readonly IActiveBlockGracePeriodTracker grace;
    private readonly BitcoinBlake2bPayoutContractTracker contracts;
    private BitcoinBlake2bTemplate coin;
    private int operatorConfirmations;
    private static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(30);
    private const string ReconciliationEpisode = "bitcoin-blake2b:reconciliation";

    public BitcoinBlake2bPayoutHandler(IComponentContext ctx, IConnectionFactory cf, IMapper mapper,
        IShareRepository shares, IBlockRepository blocks, IBalanceRepository balances,
        IPaymentRepository payments, IMasterClock clock, IMessageBus messages,
        IActiveBlockGracePeriodTracker grace, BitcoinBlake2bPayoutContractTracker contracts)
        : base(ctx, cf, mapper, shares, blocks, balances, payments, clock, messages, grace)
    {
        this.grace = grace ?? throw new ArgumentNullException(nameof(grace));
        this.contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
    }

    protected override string LogCategory => "Bitcoin BLAKE2b Payout Handler";

    public override async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        if(pc.Template is not BitcoinBlake2bTemplate template)
            throw new InvalidOperationException("Bitcoin BLAKE2b payout handler requires its typed coin template");
        await base.ConfigureAsync(cc, pc, ct);
        coin = template;
        operatorConfirmations = extraPoolConfig?.MinimumConfirmations ??
            template.CoinbaseMinConfimations ?? BitcoinConstants.CoinbaseMinConfimations;
    }

    protected virtual Task<RpcResponse<JObject>> ReadBlake2bPayoutContractAsync(string method, CancellationToken ct) =>
        rpcClient.ExecuteAsync<JObject>(logger, method, ct);

    private async Task<BitcoinBlake2bMaturity> AttestAsync(CancellationToken ct)
    {
        RpcResponse<JObject> identity, chain, deployment;
        try
        {
            identity = await ReadBlake2bPayoutContractAsync(BitcoinCommands.GetNetworkInfo, ct);
            chain = await ReadBlake2bPayoutContractAsync(BitcoinCommands.GetBlockchainInfo, ct);
            deployment = await ReadBlake2bPayoutContractAsync("getdeploymentinfo", ct);
        }
        catch(JsonException ex)
        {
            throw AttestationFailure(JsonFailureReason(ex));
        }
        catch(Exception ex) when(ex is HttpRequestException or TimeoutException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.RpcUnavailable);
        }
        ct.ThrowIfCancellationRequested();
        foreach(var response in new[] { identity, chain, deployment })
        {
            // RpcClient normally returns conversion failures as synthetic -500
            // errors. Inspect their structural cause, never daemon error text.
            if(response?.Error?.Code == -32601)
                throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.ContractDrift);
            if(response?.Error?.InnerException is JsonException json)
                throw AttestationFailure(JsonFailureReason(json));
        }
        if(identity?.Error != null || chain?.Error != null || deployment?.Error != null ||
            identity == null || chain == null || deployment == null)
            throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.RpcUnavailable);
        if(identity.Response == null || chain.Response == null || deployment.Response == null)
            throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.ContractDrift);
        if(chain.Response["initialblockdownload"]?.Type != JTokenType.Boolean)
            throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.ContractDrift);
        if(chain.Response["initialblockdownload"].Value<bool>())
            throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.Syncing);
        var chainName = chain.Response["chain"]?.Type == JTokenType.String ? chain.Response["chain"].Value<string>() : null;
        BitcoinBlake2bMaturity schedule;
        try
        {
            BitcoinBlake2bJobManager.ValidateDaemonIdentity(identity.Response, poolConfig.Id);
            schedule = BitcoinBlake2bMaturity.ForNetwork(coin, chainName, poolConfig.Id);
            BitcoinBlake2bJobManager.ValidateDeployment(deployment.Response,
                coin.Networks[chainName].Blake2bActivationHeight!.Value, poolConfig.Id);
            schedule.ValidateDeployment(deployment.Response, poolConfig.Id);
        }
        catch(PoolStartupException)
        {
            throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.ContractDrift);
        }
        try { contracts.Attest(poolConfig.Id, chainName, schedule); }
        catch(InvalidOperationException)
        { throw AttestationFailure(BitcoinBlake2bPayoutAttestationReason.BindingMismatch); }
        foreach(var reason in Enum.GetValues<BitcoinBlake2bPayoutAttestationReason>())
            grace.Clear(poolConfig.Id, 0, null, AttestationEpisode(reason));
        return schedule;
    }

    private static string AttestationEpisode(BitcoinBlake2bPayoutAttestationReason reason) =>
        $"bitcoin-blake2b:attestation:{reason}";

    private static BitcoinBlake2bPayoutAttestationReason JsonFailureReason(JsonException error)
    {
        // RpcClient parses framing separately and wraps envelope conversions as
        // serialization failures, even if their nested cause is a reader error.
        return error is JsonReaderException
            ? BitcoinBlake2bPayoutAttestationReason.RpcUnavailable
            : BitcoinBlake2bPayoutAttestationReason.ContractDrift;
    }

    private BitcoinBlake2bPayoutAttestationException AttestationFailure(BitcoinBlake2bPayoutAttestationReason reason)
    {
        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinBlake2bPayoutHandler.AttestAsync",
            code: (int) reason, poolId: poolConfig.Id, stage: RpcConsumerDiagnostics.Stage.Unavailable);
        foreach(var other in Enum.GetValues<BitcoinBlake2bPayoutAttestationReason>().Where(x => x != reason))
            grace.Clear(poolConfig.Id, 0, null, AttestationEpisode(other));
        var episode = AttestationEpisode(reason);
        var delay = reason is BitcoinBlake2bPayoutAttestationReason.ContractDrift or
            BitcoinBlake2bPayoutAttestationReason.BindingMismatch ? TimeSpan.Zero : AlertAfter;
        if(grace.TryAcquireNotification(poolConfig.Id, 0, null, episode, clock.Now, delay))
        {
            try
            {
                messageBus.SendMessage(new AdminNotification($"[{poolConfig.Id}] Bitcoin BLAKE2b payout attestation withheld",
                    $"Pool {poolConfig.Id}: reason {reason} ({(int) reason}). Rewards and balances are retained. " +
                    "Check node synchronization, RPC availability and the reviewed daemon/chain/deployment configuration. " +
                    "A changed process binding requires the documented stop/reconcile/restart. Sensitive RPC details are withheld."));
                grace.MarkNotificationSent(poolConfig.Id, 0, null, episode);
            }
            catch(Exception ex)
            {
                grace.ReleaseNotification(poolConfig.Id, 0, null, episode);
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinBlake2bPayoutHandler.NotifyAttestationFailure", failure: ex);
            }
        }
        return new(reason);
    }

    protected virtual Task<RpcResponse<JObject>> GetBlockHeaderAsync(string hash, CancellationToken ct) =>
        rpcClient.ExecuteAsync<JObject>(logger, "getblockheader", ct, new object[] { hash, true });

    private async Task<BitcoinBlake2bBlockHeader> ReadHeaderAsync(string hash, CancellationToken ct)
    {
        if(hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) return null;
        try
        {
            var result = await GetBlockHeaderAsync(hash, ct);
            var header = result?.Error == null ? result?.Response : null;
            if(header?["hash"]?.Type != JTokenType.String ||
                !string.Equals(header["hash"].Value<string>(), hash, StringComparison.OrdinalIgnoreCase) ||
                header["height"]?.Type != JTokenType.Integer || !uint.TryParse(header["height"].ToString(), out var height) ||
                header["confirmations"]?.Type != JTokenType.Integer || !int.TryParse(header["confirmations"].ToString(), out var confirmations) ||
                confirmations == 0 || confirmations < -1)
                return null;
            return new(hash, height, confirmations);
        }
        catch(Exception ex) when(ex is JsonException or HttpRequestException)
        {
            return null;
        }
    }

    public override async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var schedule = await AttestAsync(ct);
        var result = new List<Block>();
        var custodial = new List<Block>();
        foreach(var block in blocks)
        {
            block.BitcoinBlake2bCustodialEvidenceVerified = false;
            if(IsDirectCoinbaseSettlement(block))
            {
                block.Status = BlockStatus.Quarantined;
                if(block.DirectSubmissionState != BitcoinDirectSubmission.LegacyObserved)
                    block.DirectSubmissionState = BitcoinDirectSubmission.Quarantined;
                block.DirectSettlementLastChecked = clock.Now;
                ResetNotifications(block);
                block.NotifyBlockFoundOnUpdate = false;
                result.Add(block);
                logger.Error(() => $"[{LogCategory}] Block {block.BlockHeight} quarantined: direct-coinbase settlement requires the separately reviewed DATUM implementation");
                continue;
            }
            // An orphan is reopened only by matching active header AND wallet
            // evidence below, never merely by entering a new payout cycle.
            if(block.Status != BlockStatus.Orphaned) block.Status = BlockStatus.Pending;
            block.ConfirmationProgress = double.IsFinite(block.ConfirmationProgress)
                ? Math.Clamp(block.ConfirmationProgress, 0, Math.BitDecrement(1.0)) : 0;
            ResetNotifications(block);
            custodial.Add(block);
        }
        foreach(var page in custodial.Chunk(100))
        {
            var wallet = await GetTransactionsAsync(page, ct);
            for(var i = 0; i < page.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var block = page[i];
                // Block-index evidence remains available after body pruning.
                // Read it AFTER the wallet batch to bound stale wallet depth.
                var header = await ReadHeaderAsync(block.Hash, ct);
                if(block.Status == BlockStatus.Orphaned && header == null)
                {
                    // This is an opportunistic scan, not an unresolved pending
                    // reward. Replacement/resynced nodes may lack stale orphans.
                    ClearDelay(block);
                    logger.Debug(() => $"[{LogCategory}] Stored orphan {block.BlockHeight}: header unavailable; retained for later reconciliation");
                    result.Add(block);
                    continue;
                }
                if(header?.Confirmations == -1 && header.Height == block.BlockHeight)
                {
                    var wasOrphaned = block.Status == BlockStatus.Orphaned;
                    block.Status = BlockStatus.Orphaned;
                    block.ConfirmationProgress = 0;
                    block.Reward = 0;
                    block.NotifyBlockUnlockedOnUpdate = !wasOrphaned;
                    ClearDelay(block);
                }
                else
                {
                    Transaction transaction = null;
                    try
                    {
                        var response = wallet?.ElementAtOrDefault(i);
                        if(response?.Error == null) transaction = response?.Response?.ToObject<Transaction>();
                        if(header == null || header.Height != block.BlockHeight || transaction == null ||
                            !transaction.Generated || transaction.Confirmations <= 0 ||
                            !string.Equals(transaction.BlockHash, block.Hash, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(transaction.TxId, block.TransactionConfirmationData, StringComparison.OrdinalIgnoreCase) ||
                            !TryReward(transaction, out var reward, out var immature))
                            NotifyDelay(block);
                        else
                        {
                            var confirmations = Math.Min(transaction.Confirmations, header.Confirmations);
                            var spendable = !immature && confirmations >= schedule.RequiredConfirmations(operatorConfirmations);
                            block.Status = spendable ? BlockStatus.Confirmed : BlockStatus.Pending;
                            block.ConfirmationProgress = schedule.Progress(confirmations, operatorConfirmations, immature);
                            block.Reward = reward;
                            block.BitcoinBlake2bCustodialEvidenceVerified = true;
                            block.NotifyBlockConfirmationProgressOnUpdate = !spendable;
                            block.NotifyBlockUnlockedOnUpdate = spendable;
                            ClearDelay(block);
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight}: wallet category {(immature ? "immature" : "generate")}, confirmations {confirmations}, remaining depth {schedule.Remaining(confirmations, operatorConfirmations)}, status {block.Status}");
                        }
                    }
                    catch(Exception ex) when(ex is JsonException or OverflowException)
                    {
                        NotifyDelay(block);
                    }
                }
                result.Add(block);
            }
        }
        return result.ToArray();
    }

    internal static bool TryReward(Transaction transaction, out decimal reward, out bool immature)
    {
        reward = 0;
        immature = transaction.Details?.FirstOrDefault()?.Category == "immature";
        var category = immature ? "immature" : "generate";
        if(transaction.Details is not { Length: > 0 } || transaction.Details.Any(x =>
               x == null || x.Category != category || x.Amount <= 0 || x.Vout < 0) ||
            transaction.Details.Select(x => x.Vout).Distinct().Count() != transaction.Details.Length)
            return false;
        var credit = transaction.Details.Sum(x => x.Amount);
        // Knots top-level amount is ZERO for immature coinbases. The owned
        // output details carry their actual credit; never fabricate liquidity.
        reward = immature ? credit : transaction.Amount;
        return reward > 0 && (immature || reward == credit);
    }

    private static void ResetNotifications(Block block)
    {
        block.NotifyBlockConfirmationProgressOnUpdate = false;
        block.NotifyBlockUnlockedOnUpdate = false;
    }

    private void ClearDelay(Block block) => grace.Clear(block.PoolId, block.Id, block.Hash, ReconciliationEpisode);

    private void NotifyDelay(Block block)
    {
        if(!grace.TryAcquireNotification(block.PoolId, block.Id, block.Hash, ReconciliationEpisode, clock.Now, AlertAfter)) return;
        try
        {
            messageBus.SendMessage(new AdminNotification($"[{poolConfig.Id}] Bitcoin BLAKE2b block reconciliation delayed",
                $"Pool {poolConfig.Id} block {block.BlockHeight} has lacked matching active-chain header and wallet coinbase evidence for at least {(int) AlertAfter.TotalMinutes} minutes. " +
                "The stored reward remains unresolved. Check getblockheader/gettransaction, wallet indexing, synchronization and RPC proxies. Sensitive verification detail is withheld."));
            grace.MarkNotificationSent(block.PoolId, block.Id, block.Hash, ReconciliationEpisode);
        }
        catch(Exception ex)
        {
            grace.ReleaseNotification(block.PoolId, block.Id, block.Hash, ReconciliationEpisode);
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinBlake2bPayoutHandler.NotifyDelay", failure: ex);
        }
    }

    internal void NotifyAllocationHold(Block block)
    {
        const string episode = "bitcoin-blake2b:allocation-history";
        if(!grace.TryAcquireNotification(block.PoolId, block.Id, block.Hash, episode, clock.Now, TimeSpan.Zero)) return;
        try
        {
            messageBus.SendMessage(new AdminNotification($"[{poolConfig.Id}] Bitcoin BLAKE2b allocation recovery withheld",
                $"Pool {poolConfig.Id} block {block.BlockHeight}: a later or same-time custodial reward is already confirmed. " +
                "PROP/PPLNS share history may have been consumed and recovered funds may already have been swept. " +
                "Automatic confirmation and balance allocation are withheld. Preserve backups and audit original shares, " +
                "credits, payments and spendable wallet backing using the documented historical recovery procedure."));
            grace.MarkNotificationSent(block.PoolId, block.Id, block.Hash, episode);
        }
        catch(Exception ex)
        {
            grace.ReleaseNotification(block.PoolId, block.Id, block.Hash, episode);
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinBlake2bPayoutHandler.NotifyAllocationHold", failure: ex);
        }
    }

    public override async Task PayoutAsync(IMiningPool pool, Miningcore.Persistence.Model.Balance[] balances, CancellationToken ct)
    {
        await AttestAsync(ct);
        await base.PayoutAsync(pool, balances, ct);
    }
}
