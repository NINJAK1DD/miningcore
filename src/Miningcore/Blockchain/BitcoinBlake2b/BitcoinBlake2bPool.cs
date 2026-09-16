using Miningcore.Rpc;
using Autofac;
using AutoMapper;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        this(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus,
            rmsm, nicehashService, TimeProvider.System)
    {
    }

    // Inject once at construction: every connection uses the same immutable
    // time source, including connections whose bucket is created later.
    internal BitcoinBlake2bPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf, IStatsRepository statsRepo, IMapper mapper,
        IMasterClock clock, IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm, NicehashService nicehashService,
        TimeProvider difficultyBudgetTimeProvider) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus,
            rmsm, nicehashService)
    {
        ArgumentNullException.ThrowIfNull(difficultyBudgetTimeProvider);
        createDifficultyBudget = _ => new DifficultyRequestBudget(difficultyBudgetTimeProvider);
    }

    private readonly PoolOperationGate operations = new();
    private readonly ConditionalWeakTable<StratumConnection, SemaphoreSlim> assignmentGates = new();

    // Serialize mutations and their complete wire assignment, not individual sends.
    // Internal virtual entry allows deterministic contention barriers in TCP tests.
    internal virtual Task EnterAssignmentAsync(StratumConnection connection, CancellationToken ct) =>
        assignmentGates.GetValue(connection, static _ => new SemaphoreSlim(1, 1)).WaitAsync(ct);

    private void ExitAssignment(StratumConnection connection) => assignmentGates.GetValue(connection,
        static _ => new SemaphoreSlim(1, 1)).Release();

    private bool IsAdmissionClosed(StratumConnection connection) => operations.IsClosed ||
        difficultyBudgets.TryGetValue(connection, out var budget) && budget.IsClosed;

    // Weak connection keys retain no disconnected-miner/IP history. Suggest,
    // configure and static-difficulty authorization share one bucket, including
    // pre-subscription calls.
    private readonly ConditionalWeakTable<StratumConnection, DifficultyRequestBudget> difficultyBudgets = new();
    private readonly ConditionalWeakTable<StratumConnection, DifficultyRequestBudget>.CreateValueCallback createDifficultyBudget;
    private readonly object statusSync = new();
    private CancellationToken hostShutdown;
    private int lifetimeStarted;
    private int online;
    private int secondaryFailureReports;

    public string MiningState => Volatile.Read(ref lifetimeStarted) == 1 && hostShutdown.IsCancellationRequested ? "stopping" :
        operations.IsClosed ? (operations.ActiveCount > 0 ? "draining" : "faulted") :
        Volatile.Read(ref online) != 0 ? "online" : "starting";
    public bool MiningFaulted => operations.IsClosed;
    public IDisposable TryAcquireOperation() => operations.TryAcquire();

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        // Also enforce the loader's invariant for programmatically supplied templates.
        if(pc.Template is not BitcoinBlake2bTemplate { DisableVersionRolling: true })
            throw new PoolStartupException("Bitcoin BLAKE2b requires version rolling to be disabled", pc.Id);
        base.Configure(pc, cc);
    }

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
            try { await operations.Drained.WaitAsync(DrainReportInterval, ct); }
            catch(TimeoutException)
            {
                ct.ThrowIfCancellationRequested();
                var activeLeases = operations.ActiveCount;
                if(activeLeases > 0)
                    logger.Warn("Bitcoin BLAKE2b isolation is draining {0} outstanding admission lease(s) after {1:F0}s; nested leases are counted separately; no new work is admitted", activeLeases, elapsed.Elapsed.TotalSeconds);
            }
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
        if(hostShutdown.IsCancellationRequested)
            return;
        if(!operations.Close())
        {
            // FaultPool is serialized by statusSync. Cap production-level reports
            // without losing subsequent evidence when Debug logging is enabled.
            if(secondaryFailureReports < 3)
            {
                secondaryFailureReports++;
                logger.Info("Additional failure after pool isolation ({0}/3 Info reports; subsequent failures use Debug). Operator restart is required.", secondaryFailureReports);
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Info, "BitcoinBlake2bPool.FaultPool", failure: ex);
            }
            else
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Debug, "BitcoinBlake2bPool.FaultPool", failure: ex);
            return;
        }

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinBlake2bPool.FaultPool", failure: ex);
        logger.Error("Bitcoin BLAKE2b pool faulted; operator restart required. Other pools remain running; already-owned accounting operations are retained.");
        try
        {
            messageBus.NotifyPoolStatus(this, PoolStatus.Offline);
            messageBus.SendMessage(new AdminNotification("Bitcoin BLAKE2b pool faulted",
                $"Pool '{poolConfig.Id}' stopped issuing and accepting mining work after a local failure. Other pools remain running. Inspect its logs and review daemon compatibility before restarting. Already-owned accounting operations are retained."));
        }
        catch(Exception notificationError)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinBlake2bPool.FaultPool", failure: notificationError);
        }
    }

    protected override async Task OnNewJobAsync(object jobParams)
    {
        if(operations.IsClosed)
            return;
        logger.Info(() => $"Broadcasting base job {((object[]) jobParams)[0]} (worker IDs include a difficulty suffix)");
        async Task BroadcastAsync() => await ForEachMinerAsync(async (connection, ct) =>
        {
            await EnterAssignmentAsync(connection, ct);
            try
            {
                var context = connection.ContextAs<BitcoinWorkerContext>();
                if(IsAdmissionClosed(connection) || !context.IsSubscribed)
                    return;
                // Keep pending VarDiff, its announcement and the immutable target
                // snapshot in the same critical section as miner-driven changes.
                if(context.ApplyPendingDifficulty())
                    await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty,
                        new object[] { context.Difficulty });
                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                    CreateWorkerJob(connection, (bool) ((object[]) jobParams)[^1]));
            }
            finally { ExitAssignment(connection); }
        });
        await Guard(BroadcastAsync);
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await EnterAssignmentAsync(connection, ct);
        try
        {
            if(!IsAdmissionClosed(connection))
                await base.OnVarDiffUpdateAsync(connection, newDiff, ct);
        }
        finally { ExitAssignment(connection); }
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        if(operations.IsClosed)
        {
            Disconnect(connection);
            return;
        }

        // Submit-only or unrelated traffic does not allocate admission state.
        difficultyBudgets.TryGetValue(connection, out var budget);
        if(budget?.IsClosed == true)
            return;

        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(request.Value.Method == BitcoinStratumMethods.Subscribe && context.IsSubscribed)
        {
            budget ??= difficultyBudgets.GetValue(connection, createDifficultyBudget);
            if(budget.TryWarnDuplicateSubscribe())
                await connection.RespondErrorAsync(StratumError.Other,
                    "Already subscribed; another subscription attempt will close this connection", request.Value.Id, false);
            else
                CloseAdmission(connection, budget, StratumDiagnostics.Event.DuplicateSubscription, "duplicate-subscribe");
            return;
        }

        if(request.Value.Id == null && request.Value.Method is BitcoinStratumMethods.SuggestDifficulty or
            BitcoinStratumMethods.Authorize or BitcoinStratumMethods.MiningConfigure)
        {
            await connection.RespondErrorAsync(StratumError.MinusOne, "missing request id", null, false);
            return;
        }

        JArray extensions = null;
        double? minimumDifficulty = null;
        var malformed = request.Value.Method switch
        {
            BitcoinStratumMethods.MiningConfigure => !TryValidateConfigure(request.Value.Params, out extensions, out minimumDifficulty),
            BitcoinStratumMethods.Authorize => !IsValidAuthorization(request.Value.Params),
            _ => false,
        };
        var difficultyRequest = request.Value.Method == BitcoinStratumMethods.SuggestDifficulty || minimumDifficulty.HasValue;
        if(malformed || difficultyRequest)
        {
            budget ??= difficultyBudgets.GetValue(connection, createDifficultyBudget);
            if(!await AdmitDifficultyRequestAsync(connection, budget, request.Value, malformed ? null : extensions))
                return;
        }
        if(malformed)
        {
            await connection.RespondErrorAsync(StratumError.Other, "Invalid request parameters", request.Value.Id, false);
            return;
        }

        // Authorization runs its address-validation RPC outside the gate; only
        // ApplyStaticDifficultyAsync enters it. Share/RPC/accounting work never
        // owns this gate either (its final VarDiff update acquires it separately).
        if(request.Value.Method is not (BitcoinStratumMethods.Subscribe or
            BitcoinStratumMethods.SuggestDifficulty or BitcoinStratumMethods.MiningConfigure))
        {
            await base.OnRequestAsync(connection, request, ct);
            return;
        }

        await EnterAssignmentAsync(connection, ct);
        try
        {
            if(IsAdmissionClosed(connection))
                return;
            var previousDifficulty = context.Difficulty;
            if(request.Value.Method == BitcoinStratumMethods.MiningConfigure)
                await OnConfigureMiningAsync(connection, request, minimumDifficulty);
            else
                await base.OnRequestAsync(connection, request, ct);
            await CompleteAssignmentAsync(connection, previousDifficulty, request.Value.Method);
        }
        finally { ExitAssignment(connection); }
    }

    private async Task CompleteAssignmentAsync(StratumConnection connection, double previousDifficulty, string method)
    {
        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(context.Difficulty == previousDifficulty)
            return;
        try
        {
            Blake2bManager.ValidateWorkerDifficulty(context.Difficulty);
        }
        catch(ArgumentOutOfRangeException)
        {
            context.SetDifficulty(previousDifficulty);
            context.ClearJobs();
            Disconnect(connection);
            return;
        }
        if(context.IsSubscribed && method != BitcoinStratumMethods.Subscribe)
        {
            if(method == BitcoinStratumMethods.MiningConfigure)
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, CreateWorkerJob(connection, false));
        }
    }

    protected override async Task OnAuthorizeAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        var parameters = request.Value.ParamsAs<string[]>();
        var password = parameters?.Length > 1 ? parameters[1] : null;
        // Use exactly the inherited parser once, including legacy embedded d= syntax.
        var difficulty = GetStaticDiffFromPassparts(password?.Split(PasswordControlVarsSeparator));
        if(difficulty.HasValue && !await AdmitDifficultyRequestAsync(connection,
            difficultyBudgets.GetValue(connection, createDifficultyBudget), request.Value, null))
            return;
        await OnAuthorizeCoreAsync(connection, request, ct, new ParsedStaticDifficulty(difficulty));
    }

    protected override async Task ApplyStaticDifficultyAsync(StratumConnection connection, double? difficulty, CancellationToken ct)
    {
        if(!difficulty.HasValue)
            return;
        await EnterAssignmentAsync(connection, ct);
        try
        {
            if(IsAdmissionClosed(connection))
                return;
            var previousDifficulty = connection.Context.Difficulty;
            await base.ApplyStaticDifficultyAsync(connection, difficulty, ct);
            await CompleteAssignmentAsync(connection, previousDifficulty, BitcoinStratumMethods.Authorize);
        }
        finally { ExitAssignment(connection); }
    }

    private static bool IsValidAuthorization(object parameters) =>
        parameters is JArray { Count: 1 or 2 } values && values[0].Type == JTokenType.String &&
        (values.Count == 1 || values[1].Type is JTokenType.String or JTokenType.Null);

    private static bool TryValidateConfigure(object parameters, out JArray extensions, out double? minimumDifficulty)
    {
        extensions = null;
        minimumDifficulty = null;
        if(parameters is not JArray { Count: 2 } array || array[0] is not JArray requested ||
            requested.Any(x => x.Type != JTokenType.String) || array[1] is not JObject values)
            return false;
        if(requested.Any(x => x.Value<string>() == BitcoinStratumExtensions.MinimumDiff))
        {
            var value = values[BitcoinStratumExtensions.MinimumDiffValue];
            if(value?.Type is not (JTokenType.Integer or JTokenType.Float or JTokenType.String))
                return false;
            // Strings are an existing firmware compatibility case. Parse once
            // with invariant culture and pass this exact value to the base handler.
            var text = value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
            if(!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var difficulty) ||
                !double.IsFinite(difficulty) || difficulty <= 0)
                return false;
            minimumDifficulty = difficulty;
        }
        extensions = requested;
        return true;
    }

    private async Task<bool> AdmitDifficultyRequestAsync(StratumConnection connection, DifficultyRequestBudget budget,
        JsonRpcRequest request, JArray extensions)
    {
        var admission = budget.TryAcquire();
        if(admission == DifficultyRequestBudget.Admission.Allowed)
            return true;
        if(admission == DifficultyRequestBudget.Admission.Disconnect)
            CloseAdmission(connection, budget, StratumDiagnostics.Event.DifficultyBudgetDisconnect, "difficulty-disconnect");
        else
        {
            PublishTelemetry(TelemetryCategory.StratumAdmission, "difficulty-refused", TimeSpan.Zero);
            await RefuseDifficultyRequestAsync(connection, request, extensions);
        }
        return false;
    }

    private void CloseAdmission(StratumConnection connection, DifficultyRequestBudget budget,
        StratumDiagnostics.Event reason, string outcome)
    {
        if(!budget.TryClose())
            return;
        StratumDiagnostics.Write(logger, NLog.LogLevel.Info, reason, connection.ConnectionId);
        PublishTelemetry(TelemetryCategory.StratumAdmission, outcome, TimeSpan.Zero);
        Disconnect(connection);
    }

    private Task RefuseDifficultyRequestAsync(StratumConnection connection, JsonRpcRequest request, JArray extensions)
    {
        var reason = FormattableString.Invariant($"Difficulty request rate limit exceeded; retry after {DifficultyRequestBudget.RefillInterval.TotalSeconds} seconds");
        if(extensions == null)
            return connection.RespondErrorAsync(StratumError.Other, reason, request.Id, false);

        // BIP310 permits an extension error string. BLAKE2b supports only
        // minimum-difficulty; other extensions remain explicitly unsupported.
        var result = new Dictionary<string, object>();
        foreach(var extension in extensions)
        {
            if(extension.Type == JTokenType.String)
                result[extension.Value<string>()] = extension.Value<string>() == BitcoinStratumExtensions.MinimumDiff
                    ? reason : false;
        }
        var response = new JsonRpcResponse<object>(result, request.Id);
        // Preserve the inherited success response shape for ordinary clients,
        // including its compatibility exception for NiceHash/ASICBoost clients.
        if(connection.ContextAs<BitcoinWorkerContext>().IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object> { ["error"] = null };
        }
        return connection.RespondAsync(response);
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
