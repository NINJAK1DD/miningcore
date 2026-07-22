using System.Data;
using System.Data.Common;
using System.Threading;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using Polly;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

public abstract class PayoutHandlerBase
{
    protected PayoutHandlerBase(
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.cf = cf;
        this.mapper = mapper;
        this.clock = clock;
        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;
        this.balanceRepo = balanceRepo;
        this.paymentRepo = paymentRepo;
        this.messageBus = messageBus;

        BuildFaultHandlingPolicy();
    }

    protected readonly IBalanceRepository balanceRepo;
    protected readonly IBlockRepository blockRepo;
    protected readonly IConnectionFactory cf;
    protected readonly IMapper mapper;
    protected readonly IPaymentRepository paymentRepo;
    protected readonly IShareRepository shareRepo;
    protected readonly IMasterClock clock;
    protected readonly IMessageBus messageBus;
    private readonly AsyncLocal<PayoutReconciliationTracker> activePayout = new();
    protected ClusterConfig clusterConfig;
    private IAsyncPolicy faultPolicy;

    protected ILogger logger;
    protected PoolConfig poolConfig;
    private const int RetryCount = 8;

    protected abstract string LogCategory { get; }

    private RewardRecipient[] RewardRecipients =>
        poolConfig.RewardRecipients ?? Array.Empty<RewardRecipient>();

    protected void BuildFaultHandlingPolicy()
    {
        var retry = Policy
            .Handle<DbException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(RetryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), OnRetry);

        faultPolicy = retry;
    }

    protected virtual void OnRetry(Exception ex, TimeSpan timeSpan, int retry, object context)
    {
        logger.Warn(() => $"[{LogCategory}] Retry {1} in {timeSpan} due to: {ex}");
    }

    public virtual async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct)
    {
        var blockRewardRemaining = block.Reward;

        // Distribute funds to configured reward recipients
        foreach(var recipient in RewardRecipients.Where(x => x.Percentage > 0))
        {
            var amount = block.Reward * (recipient.Percentage / 100.0m);
            var address = recipient.Address;

            blockRewardRemaining -= amount;

            // skip transfers from pool wallet to pool wallet
            if(address != poolConfig.Address)
            {
                logger.Info(() => $"Crediting {address} with {FormatAmount(amount)}");
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for block {block.BlockHeight}");
            }
        }

        return blockRewardRemaining;
    }

    protected async Task PersistPaymentsAsync(Balance[] balances, string transactionConfirmation)
    {
        Contract.RequiresNonNull(balances);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(transactionConfirmation));

        activePayout.Value?.MarkSubmitted(balances, transactionConfirmation);

        var coin = poolConfig.Template.As<CoinTemplate>();

        try
        {
            await faultPolicy.ExecuteAsync(async () =>
            {
                await cf.RunTx(async (con, tx) =>
                {
                    if(!await paymentRepo.TryBeginPaymentBatchAsync(con, tx, poolConfig.Id,
                           transactionConfirmation, clock.Now))
                    {
                        logger.Warn(() => $"[{LogCategory}] Payment batch {transactionConfirmation} was already persisted; skipping duplicate balance reset");
                        return;
                    }

                    foreach(var balance in balances)
                    {
                        if(!string.IsNullOrEmpty(transactionConfirmation) && RewardRecipients.All(x => x.Address != balance.Address))
                        {
                            // record payment
                            var payment = new Payment
                            {
                                PoolId = poolConfig.Id,
                                Coin = coin.Symbol,
                                Address = balance.Address,
                                Amount = balance.Amount,
                                Created = clock.Now,
                                TransactionConfirmationData = transactionConfirmation
                            };

                            await paymentRepo.InsertAsync(con, tx, payment);
                        }

                        // reset balance
                        logger.Info(() => $"[{LogCategory}] Resetting balance of {balance.Address}");
                        await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after payment");
                    }
                });
            });

            activePayout.Value?.MarkAccepted(balances, transactionConfirmation);
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Failed to persist the following payments: " +
                $"{JsonConvert.SerializeObject(balances.Where(x => x.Amount > 0).ToDictionary(x => x.Address, x => x.Amount))}");
            throw new PayoutOutcomeUncertainException(
                "Wallet submission succeeded but its payment records could not be persisted", ex);
        }
    }

    protected async Task PersistPaymentsAsync(Dictionary<Balance, string> balances)
    {
        Contract.RequiresNonNull(balances);
        Contract.Requires<ArgumentException>(balances.Count > 0);

        foreach(var payment in balances)
            activePayout.Value?.MarkSubmitted(new[] { payment.Key }, payment.Value);

        var coin = poolConfig.Template.As<CoinTemplate>();

        try
        {
            await faultPolicy.ExecuteAsync(async () =>
            {
                await cf.RunTx(async (con, tx) =>
                {
                    foreach(var group in balances.GroupBy(x => x.Value))
                    {
                        var transactionConfirmation = group.Key;

                        if(string.IsNullOrEmpty(transactionConfirmation))
                            throw new InvalidOperationException(
                                "Refusing to persist a payment batch without a wallet transaction id");

                        if(!await paymentRepo.TryBeginPaymentBatchAsync(con, tx, poolConfig.Id,
                               transactionConfirmation, clock.Now))
                        {
                            logger.Warn(() => $"[{LogCategory}] Payment batch {transactionConfirmation} was already persisted; skipping duplicate balance reset");
                            continue;
                        }

                        foreach(var kvp in group)
                        {
                            var balance = kvp.Key;

                            if(!string.IsNullOrEmpty(transactionConfirmation) && RewardRecipients.All(x => x.Address != balance.Address))
                            {
                                // record payment
                                var payment = new Payment
                                {
                                    PoolId = poolConfig.Id,
                                    Coin = coin.Symbol,
                                    Address = balance.Address,
                                    Amount = balance.Amount,
                                    Created = clock.Now,
                                    TransactionConfirmationData = transactionConfirmation
                                };

                                await paymentRepo.InsertAsync(con, tx, payment);
                            }

                            // reset balance
                            logger.Info(() => $"[{LogCategory}] Resetting balance of {balance.Address}");
                            await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after payment");
                        }
                    }
                });
            });

            foreach(var payment in balances)
                activePayout.Value?.MarkAccepted(new[] { payment.Key }, payment.Value);
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Failed to persist the following payments: " +
                $"{JsonConvert.SerializeObject(balances.Where(x => x.Key.Amount > 0).ToDictionary(x => x.Key.Address, x => x.Key.Amount))}");
            throw new PayoutOutcomeUncertainException(
                "One or more wallet submissions succeeded but their payment records could not be persisted", ex);
        }
    }

    public virtual double AdjustShareDifficulty(double difficulty)
    {
        return difficulty;
    }

    public string FormatAmount(decimal amount)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();
        return $"{amount:0.#####} {coin.Symbol}";
    }

    protected virtual void NotifyPayoutSuccess(string poolId, Balance[] balances,
        string[] txHashes, decimal? txFee, decimal? submittedAmount = null,
        decimal? precisionAdjustment = null)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();

        // admin notifications
        var explorerLinks = !string.IsNullOrEmpty(coin.ExplorerTxLink) ?
            txHashes.Select(x => string.Format(coin.ExplorerTxLink, x)).ToArray() :
            Array.Empty<string>();

        PublishPayoutNotification(new PaymentNotification(poolId, null,
            balances.Sum(x => x.Amount), coin.Symbol, balances.Length, txHashes,
            explorerLinks, txFee)
        {
            SubmittedAmount = submittedAmount,
            PrecisionAdjustment = precisionAdjustment,
        });
    }

    /// <summary>
    /// Tracks the complete selected payout batch while a handler processes pages or individual
    /// wallet submissions. If a wallet outcome becomes uncertain, known persisted, failed,
    /// in-flight and untouched recipients are attached to the exception for the manager-owned
    /// uncertainty notification.
    /// </summary>
    protected async Task TrackPayoutAsync(Balance[] balances, Func<Task> action)
    {
        Contract.RequiresNonNull(balances);
        Contract.RequiresNonNull(action);

        var previous = activePayout.Value;
        var tracker = new PayoutReconciliationTracker(balances);
        activePayout.Value = tracker;

        try
        {
            await action();
            FlushPayoutNotifications(tracker);
        }

        catch(PayoutOutcomeUncertainException ex)
        {
            throw tracker.AttachReconciliation(ex);
        }

        catch(OperationCanceledException ex) when(tracker.HasInFlight)
        {
            throw tracker.AttachReconciliation(new PayoutOutcomeUncertainException(
                "Payout processing was cancelled while one or more wallet submissions were in flight",
                ex));
        }

        catch(OperationCanceledException)
        {
            // No submission remains in flight. Any queued subset notification therefore describes
            // a conclusive, already-persisted result and is safe to publish during shutdown.
            FlushPayoutNotifications(tracker);
            throw;
        }

        catch(AggregateException ex) when(tracker.HasInFlight)
        {
            try
            {
                WalletSubmissionOutcome.RethrowIfUnknown(ex,
                    "One or more payout wallet submissions");
            }
            catch(PayoutOutcomeUncertainException uncertain)
            {
                throw tracker.AttachReconciliation(uncertain);
            }

            throw;
        }

        finally
        {
            activePayout.Value = previous;
        }
    }

    /// <summary>
    /// Marks balances immediately before invoking a wallet submission RPC. An interrupted or
    /// otherwise ambiguous call is therefore distinguishable from recipients not yet attempted.
    /// </summary>
    protected void TrackPayoutSubmission(params Balance[] balances)
    {
        activePayout.Value?.MarkAttempting(balances);
    }

    /// <summary>
    /// Clears the in-flight state after the wallet conclusively rejects a call before submission,
    /// for example when it requires an unlock followed by a retry.
    /// </summary>
    protected void TrackPayoutSubmissionNotStarted(params Balance[] balances)
    {
        activePayout.Value?.MarkNotInFlight(balances);
    }

    /// <summary>
    /// Records a conclusive per-recipient wallet rejection before other parallel submissions
    /// finish, preventing a later cancellation from reclassifying it as uncertain.
    /// </summary>
    protected void TrackPayoutFailure(Balance[] balances, string detail)
    {
        activePayout.Value?.MarkFailed(balances, detail);
    }

    /// <summary>
    /// Retains a transaction identity as soon as the wallet returns it, before deferred batch
    /// persistence. Separate per-recipient submissions must return distinct transaction ids.
    /// </summary>
    protected void TrackPayoutTransaction(Balance[] balances, string transactionId)
    {
        activePayout.Value?.MarkSubmitted(balances, transactionId);
    }

    private void FlushPayoutNotifications(PayoutReconciliationTracker tracker)
    {
        tracker.FlushNotifications(ex => logger.Error(ex, () =>
            $"[{LogCategory}] Unable to emit conclusive payout notification"));
    }

    protected virtual void NotifyPayoutFailure(string poolId, Balance[] balances,
        string error, Exception ex, decimal? submittedAmount = null,
        decimal? precisionAdjustment = null)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();

        activePayout.Value?.MarkFailed(balances, error ?? ex?.Message);

        PublishPayoutNotification(new PaymentNotification(poolId, error ?? ex?.Message,
            balances.Sum(x => x.Amount), coin.Symbol, balances.Length, null, null, null)
        {
            SubmittedAmount = submittedAmount,
            PrecisionAdjustment = precisionAdjustment,
        });
    }

    private void PublishPayoutNotification(PaymentNotification notification)
    {
        var tracker = activePayout.Value;

        if(tracker != null)
            tracker.EnqueueNotification(() => messageBus.SendMessage(notification));
        else
            messageBus.SendMessage(notification);
    }
}
