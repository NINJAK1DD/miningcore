using System.Collections.Concurrent;
using System.Data;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

/// <summary>
/// Coin agnostic payment processor
/// </summary>
public class PayoutManager : ProcessStatusBackgroundService
{
    public PayoutManager(IComponentContext ctx,
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IPayoutManagerLease payoutLease,
        IProcessStatus processStatus) : base(processStatus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(payoutLease);

        this.ctx = ctx;
        this.cf = cf;
        this.blockRepo = blockRepo;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;
        this.payoutLease = payoutLease;
        subscribeToPoolStatus = true;

        interval = TimeSpan.FromSeconds(clusterConfig.PaymentProcessing.Interval > 0 ?
            clusterConfig.PaymentProcessing.Interval : 600);
    }

    internal PayoutManager(IComponentContext ctx,
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IPayoutManagerLease payoutLease,
        IProcessStatus processStatus,
        Func<CancellationToken, Task> executeOverride,
        bool subscribeToPoolStatus) :
        this(ctx, cf, blockRepo, shareRepo, balanceRepo, clusterConfig, messageBus,
            payoutLease, processStatus)
    {
        this.executeOverride = executeOverride;
        this.subscribeToPoolStatus = subscribeToPoolStatus;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly IComponentContext ctx;
    private readonly IShareRepository shareRepo;
    private readonly IMessageBus messageBus;
    private readonly TimeSpan interval;
    private readonly ConcurrentDictionary<string, IMiningPool> pools = new();
    private readonly ClusterConfig clusterConfig;
    private readonly IPayoutManagerLease payoutLease;
    private readonly Func<CancellationToken, Task> executeOverride;
    private readonly bool subscribeToPoolStatus;
    private readonly CompositeDisposable disposables = new();
    internal static readonly TimeSpan MergedParentShareSettlementDelay =
        TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan DirectSettlementReconciliationInterval =
        TimeSpan.FromHours(1);
    internal const int DirectSettlementReconciliationBatchSize = 64;
    internal const ulong DirectSettlementReconciliationDepth = 4_032;
    internal int AttachedPoolCount => pools.Count;

#if !DEBUG
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromSeconds(15);
#endif

    public override async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if(!await payoutLease.TryAcquireAsync(ct))
        {
            var reason = payoutLease.AcquisitionFailure;

            throw new PoolStartupException(string.IsNullOrWhiteSpace(reason)
                ? "Another payout manager owns this PostgreSQL database, or a durable ownership marker remains after an unclean stop. " +
                  "Run exactly one payout/reconciliation processor per database. Clear a stale marker only after confirming the previous process is dead."
                : reason);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            // .NET 10 runs BackgroundService.ExecuteAsync entirely on a background thread.
            // Subscribe before StartAsync returns so immediate pool-online events cannot be lost.
            if(subscribeToPoolStatus)
            {
                disposables.Add(messageBus.Listen<PoolStatusNotification>()
                    .ObserveOn(TaskPoolScheduler.Default)
                    .Subscribe(OnPoolStatusNotification));
            }

            await base.StartAsync(ct);
        }

        catch
        {
            disposables.Dispose();
            await payoutLease.DisposeAsync();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await base.StopAsync(ct);
        }

        finally
        {
            disposables.Dispose();
            await payoutLease.DisposeAsync();
        }
    }

    private void AttachPool(IMiningPool pool)
    {
        pools.TryAdd(pool.Config.Id, pool);
    }

    private void OnPoolStatusNotification(PoolStatusNotification notification)
    {
        if(notification.Status == PoolStatus.Online)
            AttachPool(notification.Pool);
    }

    private async Task ProcessPoolsAsync(CancellationToken ct)
    {
        foreach(var pool in pools.Values.ToArray().Where(x => x.Config.Enabled && x.Config.PaymentProcessing.Enabled))
        {
            var poolConfig = pool.Config;

            logger.Info(() => $"Processing payments for pool {poolConfig.Id}");

            try
            {
                var family = HandleFamilyOverride(poolConfig.Template.Family, poolConfig);

                // resolve payout handler
                var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, CoinFamilyAttribute>>>>()
                    .First(x => x.Value.Metadata.SupportedFamilies.Contains(family)).Value;

                var handler = handlerImpl.Value;
                await handler.ConfigureAsync(clusterConfig, poolConfig, ct);

                // resolve payout scheme
                var scheme = ctx.ResolveKeyed<IPayoutScheme>(poolConfig.PaymentProcessing.PayoutScheme);

                await UpdatePoolBalancesAsync(pool, poolConfig, handler, scheme, ct);
                await PayoutPoolBalancesAsync(pool, poolConfig, handler, ct);
            }

            catch(PayoutOutcomeUncertainException)
            {
                throw;
            }

            catch(InvalidOperationException ex)
            {
                logger.Error(ex.InnerException ?? ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }

            catch(AggregateException ex)
            {
                switch(ex.InnerException)
                {
                    case HttpRequestException httpEx:
                        logger.Error(() => $"[{poolConfig.Id}] Payment processing failed: {httpEx.Message}");
                        break;

                    default:
                        logger.Error(ex.InnerException, () => $"[{poolConfig.Id}] Payment processing failed");
                        break;
                }
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{poolConfig.Id}] Payment processing failed");
            }
        }

        await MaintainShareAccountingRetentionAsync(ct);
    }

    internal async Task MaintainShareAccountingRetentionAsync(
        CancellationToken ct)
    {
        if(!Program.RequiresShareAccountingPersistence(clusterConfig))
            return;

        var now = DateTime.UtcNow;
        var ppsPools = clusterConfig.Pools.Where(pool => pool.Enabled &&
            pool.PaymentProcessing?.Enabled == true &&
            pool.PaymentProcessing.PayoutScheme == PayoutScheme.PPS).ToArray();
        var replayDays = clusterConfig.PaymentProcessing?
            .ShareAccountingRetentionDays ?? 30;
        var pruneBatchSize = clusterConfig.PaymentProcessing?
            .ShareAccountingPruneBatchSize ?? 50_000;

        var pruneResult = await cf.RunTx(async (con, tx) =>
        {
            var prunedShares = 0;
            var shareBacklog = false;
            foreach(var pool in ppsPools)
            {
                var cutoff = now.AddDays(-pool.PaymentProcessing
                    .PpsShareRetentionDays);
                var result = await shareRepo.PruneSharesBeforeInclusiveAsync(
                    con, tx, pool.Id, cutoff, pruneBatchSize, ct);
                prunedShares += result.PrunedRows;
                shareBacklog |= result.HasMore;
            }

            var evidence = await shareRepo.PruneShareAccountingEvidenceBeforeAsync(
                con, tx, now.AddDays(-replayDays) -
                    ShareAccounting.EvidencePruneSafetyMargin,
                pruneBatchSize, ct);
            return (Evidence: evidence, PrunedShares: prunedShares,
                ShareBacklog: shareBacklog);
        }, ct: ct);

        if(pruneResult.PrunedShares > 0)
            logger.Info(() =>
                $"Pruned {pruneResult.PrunedShares} expired PPS statistical share row(s)");
        if(pruneResult.ShareBacklog)
            logger.Warn(() =>
                "Expired PPS statistical shares remain after the bounded retention pass; " +
                "the backlog will continue draining during later payout cycles");
        if(pruneResult.Evidence.PrunedRows > 0)
            logger.Info(() =>
                $"Pruned {pruneResult.Evidence.PrunedRows} expired share-accounting evidence " +
                $"row(s) beyond the configured {replayDays}-day replay horizon plus " +
                $"the {ShareAccounting.EvidencePruneSafetyMargin.TotalDays:0}-day safety margin");
        if(pruneResult.Evidence.HasMore)
            logger.Warn(() =>
                "Expired share-accounting evidence remains after the bounded retention pass; " +
                "the backlog will continue draining during later payout cycles");
    }

    private static CoinFamily HandleFamilyOverride(CoinFamily family, PoolConfig pool)
    {
        switch(family)
        {
            case CoinFamily.Equihash:
                var equihashTemplate = pool.Template.As<EquihashCoinTemplate>();

                if(equihashTemplate.UseBitcoinPayoutHandler)
                    return CoinFamily.Bitcoin;

                break;
            
            case CoinFamily.Progpow:
            case CoinFamily.Satoshicash:
                return CoinFamily.Bitcoin;
        }

        return family;
    }

    private async Task UpdatePoolBalancesAsync(IMiningPool pool, PoolConfig poolConfig, IPayoutHandler handler, IPayoutScheme scheme, CancellationToken ct)
    {
        var blocksToClassify = await LoadBlocksForClassificationAsync(pool,
            ct);

        // classify
        var updatedBlocks = await handler.ClassifyBlocksAsync(pool,
            blocksToClassify, ct);

        if(updatedBlocks.Any())
        {
            foreach(var block in updatedBlocks.OrderBy(x => x.Created))
            {
                if(ShouldDeferMergedParentShareSettlement(block, DateTime.UtcNow))
                {
                    logger.Info(() => $"Deferring effort and status processing for merged parent block {block.BlockHeight} until its paired ordinary share has settled");
                    continue;
                }

                logger.Info(() => $"Processing payments for pool {poolConfig.Id}, block {block.BlockHeight}");

                await RunBlockUpdateTransactionAsync(poolConfig, block, async (con, tx) =>
                {
                    if(block.Status == BlockStatus.Quarantined)
                        return await blockRepo.UpdateBlockAsync(con, tx, block);

                    if(!block.Effort.HasValue)  // fill block effort if empty
                        await CalculateBlockEffortAsync(pool, poolConfig, block, handler, ct);

                    if(!block.MinerEffort.HasValue)  // fill block miner effort if empty
                        await CalculateMinerEffortAsync(pool, poolConfig, block, handler, ct);

                    switch(block.Status)
                    {
                        case BlockStatus.Confirmed:
                            return await ApplyConfirmedBlockAsync(con, tx, pool, block,
                                handler, scheme, ct);

                        case BlockStatus.Orphaned:
                        case BlockStatus.Pending:
                        case BlockStatus.Quarantined:
                            return await blockRepo.UpdateBlockAsync(con, tx, block);

                        default:
                            return false;
                    }
                });
            }
        }

        else
            logger.Info(() => $"No updated blocks for pool {poolConfig.Id}");
    }

    internal async Task<Block[]> LoadBlocksForClassificationAsync(
        IMiningPool pool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var poolConfig = pool.Config;
        // Pending rows remain the ordinary classification source. Confirmed and
        // orphaned direct settlements are additionally revisited in a bounded,
        // persisted rotation so later reorgs and reactivations remain visible.
        var pendingBlocks = await cf.Run(con =>
            blockRepo.GetPendingBlocksForPoolAsync(con, poolConfig.Id));
        if(poolConfig.Template is not BitcoinTemplate ||
           !await cf.Run(con =>
               blockRepo.HasBitcoinDirectSoloSchemaAsync(con, ct)))
            return pendingBlocks;

        var checkedBefore = DateTime.UtcNow -
            DirectSettlementReconciliationInterval;
        var tip = pool.NetworkStats?.BlockHeight ?? 0;
        var minimumBlockHeight = tip <= long.MaxValue &&
            tip > DirectSettlementReconciliationDepth
            ? (long) (tip - DirectSettlementReconciliationDepth)
            : 0L;
        var terminalDirectBlocks = await cf.Run(con =>
            blockRepo.GetBitcoinDirectBlocksForReconciliationAsync(
                con, poolConfig.Id, minimumBlockHeight, checkedBefore,
                DirectSettlementReconciliationBatchSize, ct));
        return pendingBlocks.Concat(terminalDirectBlocks).ToArray();
    }

    internal static bool ShouldDeferMergedParentShareSettlement(Block block,
        DateTime now)
    {
        if(block == null)
            return false;

        var isMergedParent = string.Equals(block.Type, "merged-parent",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(block.Type, "merged-parent-uncertain",
                StringComparison.OrdinalIgnoreCase);

        // Synchronous block-only persistence precedes the ordinary share recorder on both
        // direct and relay nodes. Always allow the normal five-second share buffer to settle
        // before effort or a terminal status can be frozen.
        return isMergedParent &&
            now - block.Created < MergedParentShareSettlementDelay;
    }

    internal async Task RunBlockUpdateTransactionAsync(PoolConfig poolConfig, Block block,
        Func<IDbConnection, IDbTransaction, Task<bool>> action)
    {
        var updated = await cf.RunTx(async (con, tx) =>
        {
            // Serialize classification and crediting on the persisted block row. A second
            // process that classified the same pending block must wait, then observes the
            // committed terminal status and performs no balance changes or notifications.
            var persisted = await blockRepo.GetBlockByIdForUpdateAsync(con, tx, block.Id);

            if(persisted == null)
                return false;

            if(persisted.Status != BlockStatus.Pending &&
               !CanReconcileDirectBlock(persisted, block))
            {
                // A concurrent immutable-evidence change must not let one stale
                // row monopolize the bounded reconciliation prefix forever.
                // Touch only the scan timestamp; do not apply its classification.
                if(BitcoinPayoutHandler.IsDirectCoinbaseSettlement(persisted) &&
                   persisted.Status is (BlockStatus.Confirmed or
                       BlockStatus.Orphaned) &&
                   block.DirectSettlementLastChecked.HasValue)
                    await blockRepo.TouchBitcoinDirectReconciliationAsync(con,
                        tx, persisted.Id,
                        block.DirectSettlementLastChecked.Value);

                return false;
            }

            return await action(con, tx);
        });

        if(updated && block.NotifyBlockFoundOnUpdate)
            TryNotifyPostCommit(poolConfig.Id, block, "block-found",
                () => messageBus.NotifyBlockFound(poolConfig.Id, block, poolConfig.Template));

        if(updated && block.NotifyBlockConfirmationProgressOnUpdate)
            TryNotifyPostCommit(poolConfig.Id, block, "block-confirmation-progress",
                () => messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block,
                    poolConfig.Template));

        if(updated && block.NotifyBlockUnlockedOnUpdate)
            TryNotifyPostCommit(poolConfig.Id, block, "block-unlocked",
                () => messageBus.NotifyBlockUnlocked(poolConfig.Id, block, poolConfig.Template));
    }

    internal static bool CanReconcileDirectBlock(Block persisted,
        Block classified)
    {
        if(persisted?.Status is not (BlockStatus.Confirmed or
               BlockStatus.Orphaned) ||
           classified?.Status is not (BlockStatus.Pending or
               BlockStatus.Confirmed or BlockStatus.Orphaned or
               BlockStatus.Quarantined) ||
           !BitcoinPayoutHandler.IsDirectCoinbaseSettlement(persisted) ||
           !BitcoinPayoutHandler.IsDirectCoinbaseSettlement(classified))
            return false;

        // The row lock is also an immutable-evidence check. If anything changed
        // between classification and commit, fail closed and let a later cycle
        // classify the current persisted record.
        return string.Equals(persisted.PoolId, classified.PoolId,
                   StringComparison.Ordinal) &&
               persisted.BlockHeight == classified.BlockHeight &&
               string.Equals(persisted.Type, classified.Type,
                   StringComparison.Ordinal) &&
               string.Equals(persisted.Hash, classified.Hash,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(persisted.TransactionConfirmationData,
                   classified.TransactionConfirmationData,
                   StringComparison.OrdinalIgnoreCase) &&
               persisted.GrossRewardSatoshis ==
                   classified.GrossRewardSatoshis &&
               persisted.DirectMinerRewardSatoshis ==
                   classified.DirectMinerRewardSatoshis &&
               string.Equals(persisted.DirectMinerScriptPubKey,
                   classified.DirectMinerScriptPubKey,
                   StringComparison.Ordinal) &&
               string.Equals(persisted.DirectRecipientOutputs,
                   classified.DirectRecipientOutputs,
                   StringComparison.Ordinal);
    }

    internal async Task<bool> ApplyConfirmedBlockAsync(IDbConnection con, IDbTransaction tx,
        IMiningPool pool, Block block, IPayoutHandler handler, IPayoutScheme scheme,
        CancellationToken ct)
    {
        // A guarded transition (notably auxpow-claim promotion) must win before
        // any reward side effects are applied. Everything remains in this transaction,
        // so a later balance failure rolls the block transition back too.
        if(!await blockRepo.UpdateBlockAsync(con, tx, block))
            return false;

        // The accepted coinbase is already the complete financial settlement.
        // Persisting the terminal block state is required; creating a balance or
        // wallet payment would pay the miner/recipients a second time.
        if(BitcoinPayoutHandler.IsDirectCoinbaseSettlement(block))
            return true;

        // Blockchains that do not support block-reward payments via coinbase Tx
        // must generate balance records for all reward recipients instead.
        var blockReward = await handler.UpdateBlockRewardBalancesAsync(con, tx, pool,
            block, ct);

        await scheme.UpdateBalancesAsync(con, tx, pool, handler, block, blockReward, ct);
        return true;
    }

    private static void TryNotifyPostCommit(string poolId, Block block, string notification,
        Action action)
    {
        try
        {
            action();
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Unable to emit post-commit {notification} notification for pool {poolId}, block {block.BlockHeight} [{block.Hash}]");
        }
    }

    internal async Task PayoutPoolBalancesAsync(IMiningPool pool, PoolConfig config,
        IPayoutHandler handler, CancellationToken ct)
    {
        var poolBalancesOverMinimum = await cf.Run(con =>
            balanceRepo.GetPoolBalancesOverThresholdAsync(con, config.Id, config.PaymentProcessing.MinimumPayment));

        if(poolBalancesOverMinimum.Length > 0)
        {
            payoutLease.BeginFinancialOperation();

            try
            {
                await handler.PayoutAsync(pool, poolBalancesOverMinimum, ct);
                payoutLease.CompleteFinancialOperation();
            }

            catch(PayoutOutcomeUncertainException ex)
            {
                payoutLease.MarkFinancialOutcomeUncertain();

                try
                {
                    await NotifyPayoutUncertainAsync(poolBalancesOverMinimum, config, ex);
                }

                catch(Exception notificationEx)
                {
                    logger.Error(notificationEx, () =>
                        $"Unable to emit payout-uncertain notification for pool {config.Id}");
                }

                throw;
            }

            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                // Cancellation reaching this branch may be pre-submission or may follow a
                // conclusive, fully persisted partial batch. A handler must classify every
                // ambiguous wallet outcome as PayoutOutcomeUncertainException instead, so no
                // ambiguity remains here and durable ownership can be released safely.
                payoutLease.CompleteFinancialOperation();
                throw;
            }

            catch(Exception ex)
            {
                // No handler may throw an ordinary exception after an ambiguous submission.
                // Such outcomes must be classified explicitly above. This path is therefore a
                // conclusive pre-submission/configuration failure and must not strand ownership.
                payoutLease.CompleteFinancialOperation();

                try
                {
                    await NotifyPayoutFailureAsync(poolBalancesOverMinimum, config, ex);
                }

                catch(Exception notificationEx)
                {
                    logger.Error(notificationEx, () =>
                        $"Unable to emit payout-failure notification for pool {config.Id}");
                }

                throw;
            }
        }

        else
            logger.Info(() => $"No balances over configured minimum payout for pool {config.Id}");
    }

    private Task NotifyPayoutFailureAsync(Balance[] balances, PoolConfig pool, Exception ex)
    {
        messageBus.SendMessage(new PaymentNotification(pool.Id, ex.Message,
            balances.Sum(x => x.Amount), pool.Template.Symbol, balances.Length,
            null, null, null));

        return Task.CompletedTask;
    }

    private Task NotifyPayoutUncertainAsync(Balance[] balances, PoolConfig pool,
        PayoutOutcomeUncertainException ex)
    {
        var reconciliation = ex.Reconciliation ?? new PayoutReconciliation
        {
            Uncertain = balances.Select(x => new PayoutReconciliationEntry
            {
                Address = x.Address,
                Amount = x.Amount,
                Detail = ex.Message,
            }).ToArray(),
        };

        var attemptedEntries = (reconciliation.Accepted ??
                Array.Empty<PayoutReconciliationEntry>())
            .Concat(reconciliation.Failed ?? Array.Empty<PayoutReconciliationEntry>())
            .Concat(reconciliation.Uncertain ?? Array.Empty<PayoutReconciliationEntry>())
            .Where(x => x.SubmittedAmount.HasValue)
            .ToArray();
        var submittedAmount = attemptedEntries.Length > 0
            ? attemptedEntries.Sum(x => x.SubmittedAmount.Value)
            : (decimal?) null;
        var precisionAdjustment = attemptedEntries.Length > 0
            ? attemptedEntries.Sum(x => x.SubmittedAmount.Value - x.Amount)
            : (decimal?) null;

        messageBus.SendMessage(new PaymentNotification(pool.Id, ex.Message,
            balances.Sum(x => x.Amount), pool.Template.Symbol, balances.Length,
            null, null, null)
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            Reconciliation = reconciliation,
            SubmittedAmount = submittedAmount,
            PrecisionAdjustment = precisionAdjustment,
        });

        return Task.CompletedTask;
    }

    private async Task CalculateBlockEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        // get share date-range
        var from = DateTime.MinValue;
        var to = block.Created;

        // get last block for pool
        var lastBlock = await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
            BlockStatus.Quarantined,
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

        block.Effort = await cf.Run(con =>
            shareRepo.GetEffectiveAccumulatedShareDifficultyBetweenAsync(con, pool.Config.Id, from, to, ct));

        if(block.Effort.HasValue)
            block.Effort = handler.AdjustBlockEffort(block.Effort.Value);
    }

    private async Task CalculateMinerEffortAsync(IMiningPool pool, PoolConfig poolConfig, Block block, IPayoutHandler handler, CancellationToken ct)
    {
        // get share date-range
        var from = DateTime.MinValue;
        var to = block.Created;

	var miner = block.Miner;

        // get last block for pool even for "MinerEffort". We use the same method as pool effort because adding miner address in the equation will just create an overlap in the final calculation
        var lastBlock = await cf.Run(con => blockRepo.GetBlockBeforeAsync(con, poolConfig.Id, new[]
        {
            BlockStatus.Confirmed,
            BlockStatus.Orphaned,
            BlockStatus.Pending,
            BlockStatus.Quarantined,
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

	block.MinerEffort = await cf.Run(con => shareRepo.GetMinerShareDifficultyBetweenAsync(con, pool.Config.Id, miner, from, to, ct));

        if(block.MinerEffort.HasValue)
            block.MinerEffort = handler.AdjustBlockEffort(block.MinerEffort.Value);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken ct)
    {
        if(executeOverride != null)
        {
            await executeOverride(ct);
            return;
        }

        try
        {
            logger.Info(() => "Online");

            // Allow all pools to actually come up before the first payment processing run
            await Task.Delay(initialRunDelay, ct);

            using var timer = new PeriodicTimer(interval);

            do
            {
                // Refuse to begin another cycle if either the advisory session or durable
                // ownership token has been lost. Replacement startup remains fail-closed.
                await payoutLease.EnsureHeldAsync(ct);

                try
                {
                    await ProcessPoolsAsync(ct);
                }

                catch(PayoutOutcomeUncertainException ex)
                {
                    logger.Fatal(ex, () => "Payout processing stopped with an unknown wallet outcome. Durable ownership will be retained until wallet reconciliation");
                    throw;
                }

                catch(OperationCanceledException)
                {
                    // ignored
                }

                catch(Exception ex)
                {
                    logger.Error(ex);
                }
            } while(await timer.WaitForNextTickAsync(ct));

            logger.Info(() => "Offline");
        }

        finally
        {
            disposables.Dispose();
        }
    }
}
