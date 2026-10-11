using Miningcore.Rpc;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Banning;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using NLog;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

// ReSharper disable InconsistentlySynchronizedField

namespace Miningcore.Mining;

public abstract class PoolBase : StratumServer,
    IMiningPool
{
    protected PoolBase(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) : base(ctx, messageBus, rmsm, clock)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(serializerSettings);
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(statsRepo);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(nicehashService);

        this.serializerSettings = serializerSettings;
        this.cf = cf;
        blocksRepo = ctx.Resolve<IBlockRepository>();
        shareRepo = ctx.Resolve<IShareRepository>();
        this.statsRepo = statsRepo;
        this.mapper = mapper;
        this.nicehashService = nicehashService;
    }

    protected PoolStats poolStats = new();
    protected readonly JsonSerializerSettings serializerSettings;
    protected readonly IConnectionFactory cf;
    protected readonly IBlockRepository blocksRepo;
    protected readonly IShareRepository shareRepo;
    protected readonly IStatsRepository statsRepo;
    protected readonly IMapper mapper;
    protected readonly NicehashService nicehashService;
    protected readonly CompositeDisposable disposables = new();
    protected BlockchainStats blockchainStats;
    protected static readonly TimeSpan maxShareAge = TimeSpan.FromSeconds(6);
    protected static readonly TimeSpan loginFailureBanTimeout = TimeSpan.FromSeconds(10);
    protected static readonly Regex regexStaticDiff = new(@";?d=(\d*(\.\d+)?)", RegexOptions.Compiled);
    protected const string PasswordControlVarsSeparator = ";";
    private StratumListenerReservationSession stratumListenerReservations;
    internal TimeProvider VarDiffTimeProvider { get; set; } = TimeProvider.System;

    protected abstract Task SetupJobManager(CancellationToken ct);
    protected virtual void NotifyPoolOnline()
    {
        LogPoolInfo();
        messageBus.NotifyPoolStatus(this, PoolStatus.Online);
    }
    protected abstract WorkerContextBase CreateWorkerContext();

    protected double? GetStaticDiffFromPassparts(string[] parts)
    {
        if(parts == null || parts.Length == 0)
            return null;

        foreach(var part in parts)
        {
            var m = regexStaticDiff.Match(part);

            if(m.Success)
            {
                var str = m.Groups[1].Value.Trim();
                if(double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var diff) &&
                   !double.IsNaN(diff) && !double.IsInfinity(diff))
                    return diff;
            }
        }

        return null;
    }

    protected override void OnConnect(StratumConnection connection, IPEndPoint ipEndPoint)
    {
        // setup context
        var context = CreateWorkerContext();
        var poolEndpoint = poolConfig.Ports[ipEndPoint.Port];
        var varDiff = poolConfig.EnableInternalStratum == true ? poolEndpoint.VarDiff : null;

        context.Init(poolEndpoint.Difficulty, varDiff, clock, VarDiffTimeProvider);
        connection.SetContext(context);

        // StratumConnection bounds TLS, PROXY framing and the first complete request with
        // a relative startup deadline; partial bytes no longer defeat the silence check.
    }

    #region VarDiff

    // An effective runtime bound, independent of the operator's nullable MaxDiff.
    protected virtual double MaximumVarDiff => double.MaxValue;

    protected virtual async Task UpdateVarDiffAsync(StratumConnection connection, bool idle, CancellationToken ct)
    {
        await RunAssignmentAsync(connection, () => UpdateVarDiffCoreAsync(connection, idle, ct), ct, skipIfBusy: idle);
    }

    // Call only while owning the worker's assignment gate. BLAKE2b uses this
    // core inside its terminal-publication boundary without acquiring twice.
    protected async Task UpdateVarDiffCoreAsync(StratumConnection connection, bool idle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = connection.Context;

        if(context.VarDiff != null)
        {
            logger.Debug(() => $"[{connection.ConnectionId}] Updating VarDiff{(idle ? " [IDLE]" : "")}");

            var poolEndpoint = poolConfig.Ports[connection.LocalEndpoint.Port];

            var newDiff = !idle ?
                VarDiffManager.Update(context, poolEndpoint.VarDiff, clock, MaximumVarDiff, ct) :
                VarDiffManager.IdleUpdate(context, poolEndpoint.VarDiff, clock, MaximumVarDiff, ct);

            if(newDiff != null)
            {
                logger.Info(() => $"[{connection.ConnectionId}] VarDiff update to {Math.Round(newDiff.Value, 3)}{(idle ? " [IDLE]" : "")}");

                await OnVarDiffUpdateAsync(connection, newDiff.Value, ct);
            }
        }
    }

    private async Task RunVardiffIdleUpdaterAsync(int interval, CancellationToken ct)
    {
        await Guard(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

            while(await timer.WaitForNextTickAsync(ct))
            {
                logger.Debug(() => "Vardiff Idle Update pass begins");

                await Guard(() => ForEachMinerAsync(async (connection, _ct) =>
                {
                    await UpdateIdleVarDiffAsync(connection, _ct);
                }, ct));

                logger.Debug(() => "Vardiff Idle Update pass ends");
            }
        }, ex =>
        {
            if(ex is not OperationCanceledException)
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "PoolBase.RunVardiffIdleUpdaterAsync", failure: ex);
        });
    }

    protected virtual Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken _)
    {
        connection.Context.EnqueueNewDifficulty(newDiff);

        return Task.CompletedTask;
    }

    internal Task UpdateIdleVarDiffAsync(StratumConnection connection, CancellationToken ct) =>
        Guard(() => UpdateVarDiffAsync(connection, true, ct), ex =>
        {
            if(ex is not OperationCanceledException || !ct.IsCancellationRequested)
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "PoolBase.RunVardiffIdleUpdaterAsync", failure: ex);
        });

    #endregion // VarDiff

    // Serialize calculation, state mutation and complete asynchronous publication.
    // Acquire after daemon/NiceHash lookups, never around share validation/accounting.
    // All producers for a worker use this gate even when VarDiff is replaced/disabled.
    internal virtual async ValueTask<WorkerAssignmentLease> EnterAssignmentAsync(StratumConnection connection,
        CancellationToken ct, bool skipIfBusy = false)
    {
        ct.ThrowIfCancellationRequested();
        var worker = connection.Context;
        WorkerAssignmentLease.ThrowIfNestedAssignment(worker);
        var lease = new WorkerAssignmentLease(worker);
        if(skipIfBusy)
        {
            if(!worker.AssignmentGate.Wait(0, ct))
                return null;
        }
        else
            await worker.AssignmentGate.WaitAsync(ct);
        return lease;
    }

    protected async Task RunAssignmentAsync(StratumConnection connection, Func<Task> assignment,
        CancellationToken ct = default, bool skipIfBusy = false)
    {
        var gate = await EnterAssignmentAsync(connection, ct, skipIfBusy);
        if(gate == null)
            return;
        try
        {
            gate.Activate();
            ct.ThrowIfCancellationRequested();
            if(!connection.IsDisconnectRequested)
                await assignment();
        }
        finally { gate.Release(); }
    }

    protected Task ForEachMinerAssignmentAsync(Func<StratumConnection, CancellationToken, Task> func)
    {
        // Reject at the producer, before per-miner exception handling can turn
        // inherited ownership into a pool-wide series of disconnects.
        try { WorkerAssignmentLease.ThrowIfNestedAssignment(null); }
        catch(InvalidOperationException ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "PoolBase.NestedAssignmentBroadcast", failure: ex);
            throw;
        }
        return ForEachMinerAsync((connection, ct) => RunAssignmentAsync(connection, () => func(connection, ct), ct));
    }

    protected Task ForEachMinerAsync(Func<StratumConnection, CancellationToken, Task> func)
    {
        return ForEachMinerAsync(func, CancellationToken.None);
    }

    protected async Task ForEachMinerAsync(Func<StratumConnection, CancellationToken, Task> func, CancellationToken ct)
    {
        await Parallel.ForEachAsync(connections, ct, async (kvp, _ct) =>
        {
            var connection = kvp.Value;

            try
            {
                if(!_ct.IsCancellationRequested && connection.IsAlive && connection.Context.IsAuthorized)
                {
                    await SuspiciousMinerEffortCheck(connection, ct);
                    ZombieCheck(connection);

                    await func(connection, _ct);
                }
            }

            catch(OperationCanceledException) when(_ct.IsCancellationRequested)
            {
                // Normal shutdown, including cancellation of effort-check RPCs.
            }
            catch(Exception ex)
            {
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "PoolBase.ForEachMinerAsync", failure: ex);

                Disconnect(connection);
            }
        });
    }

    protected async Task SuspiciousMinerEffortCheck(StratumConnection connection, CancellationToken ct)
    {
        if(poolConfig.Banning?.Enabled == true && poolConfig.Banning?.MinerEffortPercent.HasValue == true && poolConfig.Banning?.MinerEffortTime.HasValue == true)
        {
            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, poolConfig.Id, ct));
            DateTime dateStart = (lastBlockTime.HasValue) ? lastBlockTime.Value : connection.Context.Created;
            var minerEffort = await cf.Run(con => shareRepo.GetMinerEffortBetweenCreatedAsync(con, poolConfig.Id, connection.Context.Miner, dateStart, clock.Now, ct));
            if(minerEffort.HasValue)
            {
                logger.Debug(() => $"[{connection.ConnectionId}] Checking effort for worker: {minerEffort.Value}%");

                if(minerEffort.Value >= poolConfig.Banning.MinerEffortPercent.Value)
                {
                    var banned = BanClient(connection, TimeSpan.FromSeconds(poolConfig.Banning.MinerEffortTime.Value));
                    if(banned)
                        logger.Info(() => $"[{connection.ConnectionId}] Banning worker for suspicious effort for {poolConfig.Banning.MinerEffortTime.Value} sec");

                    throw new Exception($"Detected suspicious over-sharing-worker: Current effort over {poolConfig.Banning.MinerEffortPercent.Value}%; automatic ban applied: {banned}");
                }
            }
        }
    }

    protected void ZombieCheck(StratumConnection connection)
    {
        if(poolConfig.ClientConnectionTimeout > 0)
        {
            var lastActivityAgo = clock.Now - connection.Context.LastActivity;

            if(lastActivityAgo.TotalSeconds > poolConfig.ClientConnectionTimeout)
                throw new Exception($"Detected zombie-worker (idle-timeout exceeded)");
        }
    }

    protected void SetupBanManagement()
    {
        if(poolConfig.Banning?.Enabled == true)
        {
            var managerType = clusterConfig.Banning?.Manager ?? BanManagerKind.Integrated;
            banManager = ctx.ResolveKeyed<IBanManager>(managerType);
        }
    }

    protected virtual async Task InitStatsAsync(CancellationToken ct)
    {
        if(clusterConfig.ShareRelay == null)
            await LoadStatsAsync(ct);
    }

    private async Task LoadStatsAsync(CancellationToken ct)
    {
        try
        {
            logger.Debug(() => "Loading pool stats");

            var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolConfig.Id, ct));

            if(stats != null)
            {
                poolStats = mapper.Map<PoolStats>(stats);
                blockchainStats = mapper.Map<BlockchainStats>(stats);
            }
        }

        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Warn, "PoolBase.LoadStatsAsync", failure: ex);
        }
    }

    protected void ConsiderBan(StratumConnection connection, WorkerContextBase context, PoolShareBasedBanningConfig config)
    {
        var totalShares = context.Stats.ValidShares + context.Stats.InvalidShares;

        if(totalShares > config.CheckThreshold)
        {
            var ratioBad = (double) context.Stats.InvalidShares / totalShares;

            if(ratioBad < config.InvalidPercent / 100.0)
            {
                // reset stats
                context.Stats.ValidShares = 0;
                context.Stats.InvalidShares = 0;
            }

            else
            {
                if(poolConfig.Banning?.Enabled == true &&
                   (clusterConfig.Banning?.BanOnInvalidShares.HasValue == false ||
                       clusterConfig.Banning?.BanOnInvalidShares == true))
                {
                    if(BanClient(connection, TimeSpan.FromSeconds(config.Time)))
                        logger.Info(() => $"[{connection.ConnectionId}] Banning worker for {config.Time} sec: {Math.Floor(ratioBad * 100)}% of the last {totalShares} shares were invalid");

                    Disconnect(connection);
                }
            }
        }
    }

    private async Task RunStratum(CancellationToken ct,
        StratumListenerReservation[] listeners)
    {
        var varDiffEnabled = listeners.Any(x =>
            x.Endpoint.PoolEndpoint.VarDiff != null);

        var tasks = new List<Task>
        {
            base.RunAsync(ct, listeners)
        };

        if(varDiffEnabled)
            tasks.Add(RunVardiffIdleUpdaterAsync(poolConfig.VardiffIdleSweepInterval ?? 30, ct));

        await Task.WhenAll(tasks);
    }

    protected virtual async Task<double?> GetNicehashStaticMinDiff(WorkerContextBase context, string coinName, string algoName,
        CancellationToken ct)
    {
        if(context.IsNicehash && clusterConfig.Nicehash?.EnableAutoDiff == true)
            return await nicehashService.GetStaticDiff(coinName, algoName, ct);

        return null;
    }

    private void LogPoolInfo()
    {
        logger.Info(() => "Pool Online");

        var msg = $@"

Mining Pool:            {poolConfig.Id}
Coin Type:              {poolConfig.Template.Symbol} [{poolConfig.Template.Symbol}]
Network Connected:      {blockchainStats.NetworkType}
Detected Reward Type:   {blockchainStats.RewardType}
Current Block Height:   {blockchainStats.BlockHeight}
Current Connect Peers:  {blockchainStats.ConnectedPeers}
Network Difficulty:     {(blockchainStats.NetworkDifficulty > 1000 ? FormatUtil.FormatQuantity(blockchainStats.NetworkDifficulty) : blockchainStats.NetworkDifficulty)}
Network Hash Rate:      {FormatUtil.FormatHashrate(blockchainStats.NetworkHashrate)}
Stratum Port(s):        {(poolConfig.Ports?.Any() == true ? string.Join(", ", poolConfig.Ports.Keys) : string.Empty)}
Pool Fee:               {(poolConfig.RewardRecipients?.Any() == true ? poolConfig.RewardRecipients.Where(x => x.Type != "dev").Sum(x => x.Percentage) : 0)}%
";

        logger.Info(() => msg);
    }

    #region API-Surface

    public PoolConfig Config => poolConfig;
    public PoolStats PoolStats => poolStats;
    public BlockchainStats NetworkStats => blockchainStats;

    public virtual void Configure(PoolConfig pc, ClusterConfig cc)
    {
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);

        logger = LogUtil.GetPoolScopedLogger(typeof(PoolBase), pc);
        poolConfig = pc;
        clusterConfig = cc;
    }

    internal void AttachStratumListenerReservations(
        StratumListenerReservationSession reservations)
    {
        stratumListenerReservations = reservations ??
            throw new ArgumentNullException(nameof(reservations));
    }

    public abstract double HashrateFromShares(double shares, double interval);
    public virtual double ShareMultiplier => 1;

    public virtual async Task RunAsync(CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);

        logger.Info(() => "Starting Pool ...");
        StratumListenerReservation[] listeners = null;

        try
        {
            if(poolConfig.EnableInternalStratum == true)
            {
                if(stratumListenerReservations == null)
                {
                    throw new PoolStartupException(
                        "Internal Stratum listeners were not reserved before pool startup",
                        poolConfig.Id);
                }

                // Claim ownership before initialization can announce this pool online.
                listeners = stratumListenerReservations.Claim(poolConfig.Id);
            }

            SetupBanManagement();

            await SetupJobManager(ct);
            await InitStatsAsync(ct);

            if(listeners != null)
            {
                foreach(var listener in listeners)
                    listener.Activate();
            }

            NotifyPoolOnline();

            if(poolConfig.EnableInternalStratum == true)
                await RunStratum(ct, listeners);
            else
                await WaitForShutdownAsync(ct);
        }

        catch(PoolStartupException)
        {
            // just forward these
            throw;
        }

        catch(TaskCanceledException)
        {
            // just forward these
            throw;
        }

        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "PoolBase.RunAsync", failure: ex);
            throw;
        }

        finally
        {
            if(listeners != null)
            {
                foreach(var listener in listeners)
                    listener.Dispose();
            }

            disposables.Dispose();
            logger.Info(() => "Pool Offline");
        }
    }

    internal static async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // Normal host shutdown for pools that only maintain jobs/network state for a relay
            // receiver and therefore have no internal Stratum listener to keep RunAsync alive.
        }
    }

    #endregion // API-Surface
}
