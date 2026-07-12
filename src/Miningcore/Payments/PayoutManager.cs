using System.Collections.Concurrent;
using System.Data;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Hosting;
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
public class PayoutManager : BackgroundService
{
    public PayoutManager(IComponentContext ctx,
        IConnectionFactory cf,
        IBlockRepository blockRepo,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IPayoutManagerLease payoutLease)
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

        interval = TimeSpan.FromSeconds(clusterConfig.PaymentProcessing.Interval > 0 ?
            clusterConfig.PaymentProcessing.Interval : 600);
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
    private readonly CompositeDisposable disposables = new();

#if !DEBUG
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan initialRunDelay = TimeSpan.FromSeconds(15);
#endif

    public override async Task StartAsync(CancellationToken ct)
    {
        if(!await payoutLease.TryAcquireAsync(ct))
            throw new PoolStartupException(
                "Another payout manager owns this PostgreSQL database, or a durable ownership marker remains after an unclean stop. " +
                "Run exactly one payout/reconciliation processor per database. Clear a stale marker only after confirming the previous process is dead.");

        try
        {
            await base.StartAsync(ct);
        }

        catch
        {
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
        // get pending blockRepo for pool
        var pendingBlocks = await cf.Run(con => blockRepo.GetPendingBlocksForPoolAsync(con, poolConfig.Id));

        // classify
        var updatedBlocks = await handler.ClassifyBlocksAsync(pool, pendingBlocks, ct);

        if(updatedBlocks.Any())
        {
            foreach(var block in updatedBlocks.OrderBy(x => x.Created))
            {
                logger.Info(() => $"Processing payments for pool {poolConfig.Id}, block {block.BlockHeight}");

                await RunBlockUpdateTransactionAsync(poolConfig, block, async (con, tx) =>
                {
                    if(!block.Effort.HasValue)  // fill block effort if empty
                        await CalculateBlockEffortAsync(pool, poolConfig, block, handler, ct);

                    if(!block.MinerEffort.HasValue)  // fill block miner effort if empty
                        await CalculateMinerEffortAsync(pool, poolConfig, block, handler, ct);

                    switch(block.Status)
                    {
                        case BlockStatus.Confirmed:
                            // blockchains that do not support block-reward payments via coinbase Tx
                            // must generate balance records for all reward recipients instead
                            var blockReward = await handler.UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);

                            await scheme.UpdateBalancesAsync(con, tx, pool, handler, block, blockReward, ct);
                            return await blockRepo.UpdateBlockAsync(con, tx, block);

                        case BlockStatus.Orphaned:
                        case BlockStatus.Pending:
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

    internal async Task RunBlockUpdateTransactionAsync(PoolConfig poolConfig, Block block,
        Func<IDbConnection, IDbTransaction, Task<bool>> action)
    {
        var updated = await cf.RunTx(async (con, tx) =>
        {
            // Serialize classification and crediting on the persisted block row. A second
            // process that classified the same pending block must wait, then observes the
            // committed terminal status and performs no balance changes or notifications.
            var persisted = await blockRepo.GetBlockByIdForUpdateAsync(con, tx, block.Id);

            if(persisted == null || persisted.Status != BlockStatus.Pending)
                return false;

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

    private async Task PayoutPoolBalancesAsync(IMiningPool pool, PoolConfig config, IPayoutHandler handler, CancellationToken ct)
    {
        var poolBalancesOverMinimum = await cf.Run(con =>
            balanceRepo.GetPoolBalancesOverThresholdAsync(con, config.Id, config.PaymentProcessing.MinimumPayment));

        if(poolBalancesOverMinimum.Length > 0)
        {
            try
            {
                await handler.PayoutAsync(pool, poolBalancesOverMinimum, ct);
            }

            catch(Exception ex)
            {
                await NotifyPayoutFailureAsync(poolBalancesOverMinimum, config, ex);
                throw;
            }
        }

        else
            logger.Info(() => $"No balances over configured minimum payout for pool {config.Id}");
    }

    private Task NotifyPayoutFailureAsync(Balance[] balances, PoolConfig pool, Exception ex)
    {
        messageBus.SendMessage(new PaymentNotification(pool.Id, ex.Message, balances.Sum(x => x.Amount), pool.Template.Symbol));

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
        }, block.Created));

        if(lastBlock != null)
            from = lastBlock.Created;

	block.MinerEffort = await cf.Run(con => shareRepo.GetMinerShareDifficultyBetweenAsync(con, pool.Config.Id, miner, from, to, ct));

        if(block.MinerEffort.HasValue)
            block.MinerEffort = handler.AdjustBlockEffort(block.MinerEffort.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // monitor pool lifetime
            disposables.Add(messageBus.Listen<PoolStatusNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(OnPoolStatusNotification));

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
