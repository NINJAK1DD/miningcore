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
    // Never hold this gate across daemon RPC or external HTTP lookups.
    // Callers release the returned instance only after successful acquisition.
    // Internal virtual entry provides deterministic contention barriers in tests.
    // Weak ownership allows reclamation with the connection;
    // do not dispose while waiters exist. AvailableWaitHandle is never used.
    internal virtual async ValueTask<SemaphoreSlim> EnterAssignmentAsync(StratumConnection connection, CancellationToken ct)
    {
        var gate = assignmentGates.GetValue(connection, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return gate;
    }

    private bool IsAdmissionClosed(StratumConnection connection) => connection.IsDisconnectRequested || operations.IsClosed ||
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

    internal void HandleBlake2bPipelineFailure(Exception ex)
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
            var gate = await EnterAssignmentAsync(connection, ct);
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
            catch(OperationCanceledException ex) when(ex is StratumConnectionClosedException or BitcoinJobRegistryClosedException)
            {
                // Submissions do not take the assignment gate. A concurrent ban
                // can close the registry while this broadcast constructs work.
                CloseAssignmentPublicationFailure(connection, ex, reportFailure: false);
            }
            finally { gate.Release(); }
        });
        await Guard(BroadcastAsync);
    }

    protected override double MaximumVarDiff => BitcoinBlake2bDifficulty.Maximum;

    protected override async Task UpdateVarDiffAsync(StratumConnection connection, bool idle, CancellationToken ct)
    {
        // Include the enabled-state check and calculation in the assignment
        // transition. A fixed-difficulty request must not disable VarDiff while
        // a calculation based on the previous assignment is awaiting publication.
        var gate = await EnterAssignmentAsync(connection, ct);
        try
        {
            if(IsAdmissionClosed(connection))
                return;
            ct.ThrowIfCancellationRequested();
            try
            {
                await base.UpdateVarDiffAsync(connection, idle, ct);
            }
            catch(Exception ex)
            {
                // Unlike the recovery loops in RunAsync, this boundary attempts
                // terminal cleanup even for OOM, then rethrows; it never resumes
                // mining with a possibly partial assignment.
                // Idle updates have no request-error boundary. Publication can
                // fail after difficulty commits but before its job is queued;
                // latch closed while still holding the assignment gate.
                // Cancellation after entering the operation also invalidates any
                // partial assignment, but ordinary shutdown is not a failure metric.
                // Use the owning operation's shutdown state, not token identity:
                // linked/wrapped cancellation can carry a different token. An
                // unrelated cancellation racing shutdown is also suppressed;
                // this affects diagnostics only, never terminal invalidation.
                CloseAssignmentPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && (ct.IsCancellationRequested || operations.IsClosed)));
                throw;
            }
        }
        finally { gate.Release(); }
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        if(connection.IsDisconnectRequested || operations.IsClosed)
        {
            GuardPublicationCleanup(connection, () => Disconnect(connection));
            return;
        }

        // Submit-only or unrelated traffic does not allocate admission state.
        difficultyBudgets.TryGetValue(connection, out var budget);
        if(budget?.IsClosed == true)
            return;

        // Missing IDs never consume admission or duplicate-subscription state,
        // including after a successful subscription or its first duplicate warning.
        if(request.Value.Id == null && request.Value.Method is BitcoinStratumMethods.SuggestDifficulty or
            BitcoinStratumMethods.Authorize or BitcoinStratumMethods.MiningConfigure or BitcoinStratumMethods.Subscribe)
        {
            await connection.RespondErrorAsync(StratumError.MinusOne, "missing request id", null, false);
            return;
        }

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

        JArray extensions = null;
        double? minimumDifficulty = null;
        var malformed = request.Value.Method switch
        {
            BitcoinStratumMethods.MiningConfigure => !TryValidateConfigure(request.Value.Params, out extensions, out minimumDifficulty),
            BitcoinStratumMethods.Authorize => !IsValidAuthorization(request.Value.Params),
            BitcoinStratumMethods.Subscribe => !IsValidSubscribe(request.Value.Params),
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

        var responseSequence = connection.ResponseSequence;
        try
        {
            PreparedSubscription? subscription = null;
            if(request.Value.Method == BitcoinStratumMethods.Subscribe)
            {
                // Resolve external data before changing subscription/extranonce
                // state. An already-authorized miner must not stall broadcasts
                // behind a cold NiceHash HTTP lookup.
                var userAgent = ReadSubscribeUserAgent(request.Value);
                var lookupContext = new BitcoinWorkerContext { UserAgent = userAgent };
                // Configure requires a BitcoinBlake2bTemplate, a BitcoinTemplate
                // subtype; this is the same template used by BitcoinPool.
                var template = (BitcoinTemplate) poolConfig.Template;
                subscription = new PreparedSubscription(userAgent,
                    await GetNicehashStaticMinDiff(lookupContext, template.Name, template.GetAlgorithmName()));
            }

            var gate = await EnterAssignmentAsync(connection, ct);
            var previousDifficulty = context.Difficulty;
            var previousSubscription = context.IsSubscribed;
            var previousExtraNonce = context.ExtraNonce1;
            var previousVarDiff = context.VarDiff;
            try
            {
                if(IsAdmissionClosed(connection))
                    return;
                ct.ThrowIfCancellationRequested();
                try
                {
                    var suggestedDifficulty = request.Value.Method == BitcoinStratumMethods.SuggestDifficulty
                        ? ReadSuggestedDifficulty(request.Value, invariant: true) : null;
                    var proposedDifficulty = minimumDifficulty ?? suggestedDifficulty;
                    if(proposedDifficulty > poolConfig.Ports[connection.LocalEndpoint.Port].Difficulty &&
                       !await ValidateProposedDifficultyAsync(connection, request.Value, proposedDifficulty.Value))
                        return;
                    if(request.Value.Method == BitcoinStratumMethods.MiningConfigure)
                        await OnConfigureMiningAsync(connection, request, minimumDifficulty);
                    else if(request.Value.Method == BitcoinStratumMethods.Subscribe)
                        // Intentionally bypass OnSubscribeAsync: BLAKE2b owns the
                        // preparation/commit boundary here. Overrides of that inherited
                        // hook do not customize this pool's subscription dispatch.
                        await OnSubscribeCoreAsync(connection, request, subscription);
                    else
                        await OnSuggestDifficultyAsync(connection, request, new ParsedSuggestedDifficulty(suggestedDifficulty));
                    await CompleteAssignmentAsync(connection, previousDifficulty, request.Value.Method);
                }
                catch(Exception ex)
                {
                    // A protocol rejection before response/assignment mutation is
                    // recoverable. All other failures invalidate the session while
                    // the gate is owned, including a failed response enqueue.
                    // In these handlers, AddJob is reached only after a response
                    // attempt or difficulty change; VersionRollingMask stays null
                    // because BLAKE2b requires DisableVersionRolling at startup.
                    // A new gated handler must witness every pre-response state
                    // mutation here, or attempt its response before mutating it.
                    if(ex is not StratumException || connection.ResponseSequence != responseSequence ||
                       context.Difficulty != previousDifficulty || context.IsSubscribed != previousSubscription ||
                       context.ExtraNonce1 != previousExtraNonce || context.VarDiff != previousVarDiff)
                        CloseAssignmentPublicationFailure(connection, ex,
                            !(ex is OperationCanceledException && (ct.IsCancellationRequested || operations.IsClosed)));
                    throw;
                }
            }
            finally { gate.Release(); }
        }
        catch(StratumException ex)
        {
            await OnRequestErrorAsync(connection, request.Value, ex, connection.ResponseSequence != responseSequence, ct);
        }
    }

    protected override Task OnRequestErrorAsync(StratumConnection connection, JsonRpcRequest request,
        StratumException error, bool responseStarted, CancellationToken ct)
    {
        if(responseStarted || IsAdmissionClosed(connection))
        {
            // Inherited handlers may acknowledge before constructing work. Once
            // that happens, a publication failure is terminal: never send a second
            // response or retain a live, partially published assignment. Also latch
            // admission closed so requests already buffered cannot resume this session.
            CloseAssignmentPublicationFailure(connection, error, reportFailure: responseStarted);
            return Task.CompletedTask;
        }

        return base.OnRequestErrorAsync(connection, request, error, false, ct);
    }

    protected override void CloseRequestPublicationFailure(StratumConnection connection, Exception failure,
        bool reportFailure = true) => CloseAssignmentPublicationFailure(connection, failure, reportFailure);

    private void CloseAssignmentPublicationFailure(StratumConnection connection, Exception failure, bool reportFailure = true)
    {
        // Even a submit-only session needs the latch: disconnect alone does not
        // prevent dispatch of further lines already in the receive buffer.
        try
        {
            GuardPublicationCleanup(connection, () =>
            {
                var budget = difficultyBudgets.GetValue(connection, createDifficultyBudget);
                budget.TryClose();
            });
        }
        finally { base.CloseRequestPublicationFailure(connection, failure, reportFailure); }
    }

    private async Task<bool> ValidateProposedDifficultyAsync(StratumConnection connection, JsonRpcRequest request,
        double difficulty)
    {
        try
        {
            Blake2bManager.ValidateWorkerDifficulty(difficulty);
            return true;
        }
        catch(ArgumentOutOfRangeException)
        {
            await connection.RespondErrorAsync(StratumError.Other,
                "Difficulty is outside the supported BLAKE2b range", request.Id, false);
            return false;
        }
    }

    private async Task CompleteAssignmentAsync(StratumConnection connection, double previousDifficulty, string method)
    {
        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(context.Difficulty == previousDifficulty)
            return;
        // Retain this final invariant: authorization releases the gate across
        // daemon RPC, so its earlier applicability check can become stale. Also
        // cover externally supplied autodiff and future assignment producers.
        try
        {
            Blake2bManager.ValidateWorkerDifficulty(context.Difficulty);
        }
        catch(ArgumentOutOfRangeException ex)
        {
            context.SetDifficulty(previousDifficulty);
            CloseAssignmentPublicationFailure(connection, ex);
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
        // Only consumed fields are converted; ignored trailing fields may contain
        // any JSON value, as with other extensible request arrays.
        var parameters = ((JArray) request.Value.Params).Take(2).Select(x => x.Value<string>()).ToArray();
        var password = parameters?.Length > 1 ? parameters[1] : null;
        // Use exactly the inherited parser once, including legacy embedded d= syntax.
        var difficulty = GetStaticDiffFromPassparts(password?.Split(PasswordControlVarsSeparator));
        if(difficulty.HasValue && !await AdmitDifficultyRequestAsync(connection,
            difficultyBudgets.GetValue(connection, createDifficultyBudget), request.Value, null))
            return;
        if(difficulty.HasValue)
        {
            // An early rejection optimization, not the final assignment guarantee.
            // VarDiff/difficulty may change after this gate is released for RPC;
            // CompleteAssignmentAsync must still validate the eventual assignment.
            var gate = await EnterAssignmentAsync(connection, ct);
            try
            {
                if(IsAdmissionClosed(connection))
                    return;
                var context = connection.ContextAs<BitcoinWorkerContext>();
                if(ShouldApplyStaticDifficulty(context, difficulty) &&
                   !await ValidateProposedDifficultyAsync(connection, request.Value, difficulty.Value))
                    return;
            }
            finally { gate.Release(); }
        }
        var responseSequence = connection.ResponseSequence;
        try
        {
            await OnAuthorizeCoreAsync(connection, request, ct, new ParsedStaticDifficulty(difficulty), parameters);
        }
        catch(Exception ex) when(connection.ResponseSequence != responseSequence)
        {
            // Authorization responds before acquiring the static-assignment gate.
            // Cover failure of that response and cancellation waiting for the gate;
            // daemon validation itself remains outside this terminal boundary.
            var gate = await EnterAssignmentAsync(connection, CancellationToken.None);
            try
            {
                CloseAssignmentPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && (ct.IsCancellationRequested || operations.IsClosed)));
            }
            finally { gate.Release(); }
            throw;
        }
    }

    protected override async Task ApplyStaticDifficultyAsync(StratumConnection connection, double? difficulty, CancellationToken ct)
    {
        if(!difficulty.HasValue)
            return;
        var gate = await EnterAssignmentAsync(connection, ct);
        try
        {
            if(IsAdmissionClosed(connection))
                return;
            var previousDifficulty = connection.Context.Difficulty;
            try
            {
                // Authorization already acknowledged success. Even cancellation
                // before static mutation leaves that assignment incomplete, so
                // close here; unlike pre-request/pre-VarDiff cancellation, it
                // cannot preserve the session. Host shutdown suppresses telemetry.
                ct.ThrowIfCancellationRequested();
                await base.ApplyStaticDifficultyAsync(connection, difficulty, ct);
                await CompleteAssignmentAsync(connection, previousDifficulty, BitcoinStratumMethods.Authorize);
            }
            catch(Exception ex)
            {
                CloseAssignmentPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && (ct.IsCancellationRequested || operations.IsClosed)));
                throw;
            }
        }
        finally { gate.Release(); }
    }

    // An omitted/null parameter list is an empty subscription, just like [].
    // Reject nested containers before the inherited string-array conversion.
    private static bool IsValidSubscribe(object parameters) =>
        parameters is null or JValue { Type: JTokenType.Null } ||
        parameters is JArray values && values.All(IsStringConvertibleScalar);

    private static bool IsValidAuthorization(object parameters) =>
        parameters is JArray { Count: >= 1 } values && IsStringConvertibleScalar(values[0]) &&
        (values.Count == 1 || IsStringConvertibleScalar(values[1]));

    // Preserve historical scalar conversion, including Boolean -> "True"/"False"
    // and ISO-shaped strings parsed by Json.NET as Date tokens. Authorization still
    // validates the converted miner address through the daemon.
    private static bool IsStringConvertibleScalar(JToken value) =>
        value.Type is JTokenType.String or JTokenType.Integer or JTokenType.Float or JTokenType.Boolean or JTokenType.Null or JTokenType.Date;

    private static bool TryValidateConfigure(object parameters, out JArray extensions, out double? minimumDifficulty)
    {
        extensions = null;
        minimumDifficulty = null;
        if(parameters is not JArray { Count: >= 2 } array || array[0] is not JArray requested ||
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
