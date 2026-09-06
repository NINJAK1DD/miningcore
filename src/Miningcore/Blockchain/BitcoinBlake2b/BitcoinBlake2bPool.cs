using Autofac;
using AutoMapper;
using System.Diagnostics;
using Microsoft.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.BitcoinBlake2b;

[CoinFamily(CoinFamily.BitcoinBlake2b)]
public class BitcoinBlake2bPool : BitcoinPool, IIsolatedMiningPool
{
    public BitcoinBlake2bPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf, IStatsRepository statsRepo, IMapper mapper,
        IMasterClock clock, IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm, NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus,
            rmsm, nicehashService)
    {
    }

    private readonly PoolOperationGate operations = new();
    private readonly object statusSync = new();
    private CancellationToken hostShutdown;
    private int lifetimeStarted;
    private int online;

    public string MiningState => Volatile.Read(ref lifetimeStarted) == 1 && hostShutdown.IsCancellationRequested ? "stopping" :
        operations.IsClosed ? (operations.ActiveCount > 0 ? "draining" : "faulted") :
        Volatile.Read(ref online) != 0 ? "online" : "starting";
    public IDisposable TryAcquireOperation() => operations.TryAcquire();

    protected override void NotifyPoolOnline()
    {
        lock(statusSync)
        {
            if(operations.IsClosed)
                throw new PoolStartupException("Bitcoin BLAKE2b pool faulted during startup", poolConfig.Id);
            Volatile.Write(ref online, 1);
            base.NotifyPoolOnline();
        }
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        // Reserve the one-shot lifetime before publishing its token. A second
        // caller must neither start another lifetime nor overwrite the first token.
        if(Interlocked.CompareExchange(ref lifetimeStarted, -1, 0) != 0)
            throw new InvalidOperationException("Bitcoin BLAKE2b pool lifetime can only be started once");
        hostShutdown = ct;
        Volatile.Write(ref lifetimeStarted, 1);
        using var localStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lifetime = base.RunAsync(localStop.Token);
        await Task.WhenAny(lifetime, operations.Failure);

        var lifetimeObserved = lifetime.IsCompleted;
        if(lifetimeObserved)
        {
            try { await lifetime; }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { return; }
            catch(Exception ex) when(ex is not OutOfMemoryException) { HandleBlake2bPipelineFailure(ex); }
            if(ct.IsCancellationRequested)
                return;
            if(!operations.IsClosed)
                HandleBlake2bPipelineFailure(new InvalidOperationException("Bitcoin BLAKE2b pool lifetime ended unexpectedly"));
        }

        // Close listeners and stop background work, but do not cancel an owned
        // submit/RPC with this pool-local token. It must reach candidate persistence
        // and statistical/PPS admission even when the miner has disconnected.
        localStop.Cancel();
        try { if(!lifetimeObserved) await lifetime; }
        catch(OperationCanceledException) when(localStop.IsCancellationRequested) { }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {
            HandleBlake2bPipelineFailure(ex);
        }

        try { await DrainOperationsAsync(ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { return; }
        // Remaining faulted is a deliberate lifetime state, not a successful
        // early exit that would trigger Program's sibling-pool fail-fast policy.
        await WaitForShutdownAsync(ct);
    }

    internal TimeSpan DrainReportInterval { get; set; } = TimeSpan.FromSeconds(30);

    private async Task DrainOperationsAsync(CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while(!operations.Drained.IsCompleted)
        {
            logger.Warn("Bitcoin BLAKE2b isolation is draining {0} owned operation(s) after {1:F0}s; no new work is admitted", operations.ActiveCount, elapsed.Elapsed.TotalSeconds);
            try { await operations.Drained.WaitAsync(DrainReportInterval, ct); }
            catch(TimeoutException) { }
        }
        logger.Info("Bitcoin BLAKE2b isolation drain completed; operator restart required");
    }

    protected override bool IsConnectionAdmissionOpen => !operations.IsClosed;

    protected override void OnConnect(StratumConnection connection, System.Net.IPEndPoint endpoint)
    {
        if(operations.IsClosed)
            throw new StratumException(StratumError.JobNotFound, "Bitcoin BLAKE2b pool is faulted");
        base.OnConnect(connection, endpoint);
    }

    protected override void OnConnectionDrainTimeout(int pending)
    {
        if(!operations.IsClosed)
            base.OnConnectionDrainTimeout(pending);
        else
            logger.Error("Bitcoin BLAKE2b connection drain timed out with {0} task(s); local admission remains closed and owned operations remain tracked. Other pools continue running", pending);
    }

    private BitcoinBlake2bJobManager Blake2bManager => manager as BitcoinBlake2bJobManager ??
        throw new InvalidOperationException("Bitcoin BLAKE2b requires its isolated job manager");

    protected override async Task OnSubmitAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        using var operation = operations.TryAcquire();
        if(operation == null)
            throw new StratumException(StratumError.JobNotFound, "Bitcoin BLAKE2b pool is faulted; work is invalidated");
        // Check raw tokens before BitcoinPool's ParamsAs<string[]> can coerce
        // numbers or Booleans into apparently valid hexadecimal strings.
        try
        {
            ValidateSubmissionParameters(request.Value.Params);
        }
        catch(StratumException)
        {
            // Preserve the ordinary invalid-share/ban boundary even though
            // strict token validation precedes the base conversion path.
            var context = connection.ContextAs<BitcoinWorkerContext>();
            context.Stats.InvalidShares++;
            ConsiderBan(connection, context, poolConfig.Banning);
            throw;
        }
        await base.OnSubmitAsync(connection, request,
            Volatile.Read(ref lifetimeStarted) == 1 ? hostShutdown : ct);
    }

    internal static void ValidateSubmissionParameters(object parameters)
    {
        var valid = parameters switch
        {
            JArray array => array.Count == 5 && array.All(x => x.Type == JTokenType.String),
            object[] array => array.Length == 5 && array.All(x => x is string),
            _ => false,
        };
        if(!valid)
            throw new StratumException(StratumError.Other,
                "Bitcoin BLAKE2b mining.submit requires exactly five JSON strings");
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<BitcoinBlake2bJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider),
                new BitcoinBlake2bExtraNonceProvider()));
        manager.Configure(poolConfig, clusterConfig);
        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() => OnNewJobAsync(job)))
                .Concat()
                .Subscribe(_ => { }, HandleBlake2bPipelineFailure));
            await manager.Jobs.Take(1).ToTask(ct);
        }
        else
            disposables.Add(manager.Jobs.Subscribe(_ => { }, HandleBlake2bPipelineFailure));
    }

    private void HandleBlake2bPipelineFailure(Exception ex)
    {
        lock(statusSync)
            FaultPool(ex);
    }

    private void FaultPool(Exception ex)
    {
        if(hostShutdown.IsCancellationRequested || !operations.Close())
            return;

        logger.Error(ex, "Bitcoin BLAKE2b pool faulted; new work and payment operations are disabled. Other pools continue running; operator restart required");
        try
        {
            messageBus.NotifyPoolStatus(this, PoolStatus.Offline);
            messageBus.SendMessage(new AdminNotification("Bitcoin BLAKE2b pool faulted",
                $"Pool '{poolConfig.Id}' stopped issuing and accepting mining work after a local failure. Other pools remain running. Inspect its logs and review daemon compatibility before restarting. Already-owned accounting operations are retained."));
        }
        catch(Exception notificationError)
        {
            logger.Error(notificationError, "Unable to report the isolated Bitcoin BLAKE2b failure; local admission is already closed");
        }
    }

    protected override async Task OnNewJobAsync(object jobParams)
    {
        if(operations.IsClosed)
            return;
        currentJobParams = jobParams;
        logger.Info(() => $"Broadcasting base job {((object[]) jobParams)[0]} (worker IDs include a difficulty suffix)");
        async Task BroadcastAsync() => await ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();
            if(!context.IsSubscribed)
                return;
            // Unlike Bitcoin SV1, the assigned target is also inside notify.
            // Apply VarDiff before taking the immutable job/target snapshot.
            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty,
                    new object[] { context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                CreateWorkerJob(connection, (bool) ((object[]) jobParams)[^1]));
        });
        await Guard(BroadcastAsync);
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        if(operations.IsClosed)
        {
            Disconnect(connection);
            return;
        }
        var context = connection.ContextAs<BitcoinWorkerContext>();
        var previousDifficulty = context.Difficulty;
        await base.OnRequestAsync(connection, request, ct);
        if(context.Difficulty == previousDifficulty)
            return;

        try
        {
            Blake2bManager.ValidateWorkerDifficulty(context.Difficulty);
        }
        catch(ArgumentOutOfRangeException)
        {
            // A miner's malformed difficulty request must not terminate the
            // shared job pipeline or leave an impossible target installed.
            context.SetDifficulty(previousDifficulty);
            context.ClearJobs();
            Disconnect(connection);
            return;
        }

        if(context.IsSubscribed)
        {
            // Subscribe already sends both messages (including a NiceHash
            // override). Custodial authorize sends difficulty only, so it
            // still needs the fresh BLAKE2b job below.
            if(request.Value.Method == BitcoinStratumMethods.Subscribe)
                return;
            // Unlike authorize/suggest-difficulty, the inherited BIP310
            // handler changes context state without announcing difficulty.
            if(request.Value.Method == BitcoinStratumMethods.MiningConfigure)
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty,
                    new object[] { context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                CreateWorkerJob(connection, false));
        }
    }

    protected override object CreateWorkerJob(StratumConnection connection,
        bool cleanJob)
    {
        if(operations.IsClosed)
            throw new StratumException(StratumError.JobNotFound,
                "Bitcoin BLAKE2b work has been invalidated");
        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(manager.GetJobForStratum() is not BitcoinBlake2bJob job)
            throw new StratumException(StratumError.JobNotFound,
                "Bitcoin BLAKE2b job is unavailable");

        BitcoinBlake2bJob workerJob;
        try
        {
            var assignment = Blake2bManager.ValidateWorkerDifficulty(context.Difficulty);
            workerJob = job.ForDifficulty(assignment);
        }
        catch(ArgumentOutOfRangeException ex)
        {
            throw new StratumException(StratumError.Other, ex.Message);
        }
        context.AddJob(workerJob, manager.maxActiveJobs);
        return workerJob.GetJobParams(cleanJob);
    }
}
