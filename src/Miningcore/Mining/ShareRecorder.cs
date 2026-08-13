using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using NLog;
using Polly;
using Polly.CircuitBreaker;
using Contract = Miningcore.Contracts.Contract;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Mining;

/// <summary>
/// Asynchronously persist shares produced by all pools for processing by coin-specific payment processor(s)
/// </summary>
public class ShareRecorder : StartupGatedBackgroundService, IBlockCandidateRecorder,
    ISharePersistenceQueueMetricsProvider
{
    // Test-only compatibility constructor. Production DI must provide the fail-stop handler;
    // this sentinel throws if a test unexpectedly reaches the dual-durability-loss path.
    internal ShareRecorder(IConnectionFactory cf,
        IMapper mapper,
        JsonSerializerSettings jsonSerializerSettings,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        ICandidatePersistenceFailureHandler candidateFailureHandler = null) :
        this(cf, mapper, jsonSerializerSettings, shareRepo, blockRepo,
            PrepareTestRecoveryState(clusterConfig),
            messageBus, MissingShareRecoveryFailureHandler.Instance,
            MissingMiningFailStopCoordinator.Instance,
            new TestShareRecoveryPathOwnership(
                ShareRecoveryFatalState.ResolveRecoveryFilename(clusterConfig)),
            candidateFailureHandler)
    {
    }

    internal ShareRecorder(IConnectionFactory cf,
        IMapper mapper,
        JsonSerializerSettings jsonSerializerSettings,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IShareRecoveryFailureHandler recoveryFailureHandler,
        IMiningFailStopCoordinator failStopCoordinator,
        ICandidatePersistenceFailureHandler candidateFailureHandler) :
        this(cf, mapper, jsonSerializerSettings, shareRepo, blockRepo,
            PrepareTestRecoveryState(clusterConfig), messageBus,
            recoveryFailureHandler, failStopCoordinator,
            new TestShareRecoveryPathOwnership(
                ShareRecoveryFatalState.ResolveRecoveryFilename(clusterConfig)),
            candidateFailureHandler)
    {
    }

    // Test-only compatibility constructor for fixtures that exercise the real failure handler.
    // Production DI selects the public constructor and supplies its singleton ownership guard.
    internal ShareRecorder(IConnectionFactory cf,
        IMapper mapper,
        JsonSerializerSettings jsonSerializerSettings,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IShareRecoveryFailureHandler recoveryFailureHandler,
        IMiningFailStopCoordinator failStopCoordinator) :
        this(cf, mapper, jsonSerializerSettings, shareRepo, blockRepo,
            PrepareTestRecoveryState(clusterConfig), messageBus,
            recoveryFailureHandler, failStopCoordinator,
            new TestShareRecoveryPathOwnership(
                ShareRecoveryFatalState.ResolveRecoveryFilename(clusterConfig)))
    {
    }

    private static ClusterConfig PrepareTestRecoveryState(ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Tests using the compatibility constructor own temporary recovery files but do not run
        // beneath systemd's STATE_DIRECTORY. Keep their independent anchor beside that temporary
        // fixture rather than writing to the developer profile. Production DI cannot select this
        // constructor because it supplies IShareRecoveryFailureHandler.
        if(string.IsNullOrWhiteSpace(config.ShareRecoveryStateDirectory))
            config.ShareRecoveryStateDirectory = Path.GetDirectoryName(
                ShareRecoveryFatalState.ResolveRecoveryFilename(config));

        return config;
    }

    public ShareRecorder(IConnectionFactory cf,
        IMapper mapper,
        JsonSerializerSettings jsonSerializerSettings,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus,
        IShareRecoveryFailureHandler recoveryFailureHandler,
        IMiningFailStopCoordinator failStopCoordinator,
        IShareRecoveryPathOwnership recoveryPathOwnership,
        ICandidatePersistenceFailureHandler candidateFailureHandler = null)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(jsonSerializerSettings);
        Contract.RequiresNonNull(messageBus);
        ArgumentNullException.ThrowIfNull(recoveryFailureHandler);
        ArgumentNullException.ThrowIfNull(failStopCoordinator);
        ArgumentNullException.ThrowIfNull(recoveryPathOwnership);

        this.cf = cf;
        this.mapper = mapper;
        this.jsonSerializerSettings = jsonSerializerSettings;
        this.messageBus = messageBus;
        this.candidateFailureHandler = candidateFailureHandler ??
            NullCandidatePersistenceFailureHandler.Instance;
        this.recoveryFailureHandler = recoveryFailureHandler;
        this.failStopCoordinator = failStopCoordinator;
        this.recoveryPathOwnership = recoveryPathOwnership;
        this.clusterConfig = clusterConfig;

        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;

        pools = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

        BuildFaultHandlingPolicy();
        recoveryFilename = ShareRecoveryFatalState.ResolveRecoveryFilename(clusterConfig);
        recoveryTerminalState = new ShareRecoveryTerminalState(recoveryFilename,
            ShareRecoveryFatalState.ResolveStateDirectory(clusterConfig));
        recoveryImportState = new ShareRecoveryImportState(recoveryFilename,
            ShareRecoveryFatalState.ResolveStateDirectory(clusterConfig));
        RecoveryTerminalStateWrite = recoveryTerminalState.Write;
        RecoveryTerminalStateRemove = recoveryTerminalState.RemoveAfterArchive;
        recoveryWriteState = RecoveryWriteStates.GetOrAdd(recoveryFilename,
            _ => new RecoveryJournalWriteState());
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IShareRepository shareRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly JsonSerializerSettings jsonSerializerSettings;
    private readonly IMessageBus messageBus;
    private readonly ICandidatePersistenceFailureHandler candidateFailureHandler;
    private readonly IShareRecoveryFailureHandler recoveryFailureHandler;
    private readonly IMiningFailStopCoordinator failStopCoordinator;
    private readonly IShareRecoveryPathOwnership recoveryPathOwnership;
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> pools;
    private readonly IMapper mapper;

    private IAsyncPolicy faultPolicy;
    private bool hasLoggedPolicyFallbackFailure;
    private string recoveryFilename;
    private const int RetryCount = 3;
    private const string PolicyContextKeyShares = "share";
    private const string PolicyContextKeyDatabaseError = "database-error";
    private const string RecoveryBatchV1StartPrefix =
        "# miningcore-recovery-batch-v1 start ";
    private const string RecoveryBatchV1EndPrefix =
        "# miningcore-recovery-batch-v1 end ";
    private const string RecoveryBatchV2StartPrefix =
        "# miningcore-recovery-batch-v2 start ";
    private const string RecoveryBatchV2EndPrefix =
        "# miningcore-recovery-batch-v2 end ";
    private const string RecoveryJournalMagicV1 =
        "# miningcore-recovery-journal-v1";
    internal const string RecoveryJournalMagic =
        "# miningcore-recovery-journal-v2";
    internal const int MaxRecoveryRecordLineLength = 1024 * 1024;
    private const string EmptyFrameDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";
    private bool notifiedAdminOnPolicyFallback = false;
    private static readonly ConcurrentDictionary<string, RecoveryJournalWriteState> RecoveryWriteStates =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private readonly RecoveryJournalWriteState recoveryWriteState;
    private readonly ShareRecoveryTerminalState recoveryTerminalState;
    private readonly ShareRecoveryImportState recoveryImportState;
    internal string RecoveryTerminalStateFilename => recoveryTerminalState.Filename;
    internal string RecoveryImportStateFilename => recoveryImportState.Filename;
    internal Action<long, string> RecoveryTerminalStateWrite { get; set; }
    internal Action RecoveryTerminalStateRemove { get; set; }
    internal Action RecoveryAnchorRemovedCheckpoint { get; set; } = () => { };
    internal Action RecoveryJournalPathValidatedCheckpoint { get; set; } =
        () => { };
    internal Action<ShareRecoveryImportState.ImportPhase>
        RecoveryImportStateWriteCheckpoint
    {
        set => recoveryImportState.WriteCheckpoint = value ?? (_ => { });
    }
    internal Action RecoveryImportStateRemoveCheckpoint
    {
        set => recoveryImportState.RemoveCheckpoint = value ?? (() => { });
    }
    internal Action RecoveryImportStateRemoveDirectorySyncCheckpoint
    {
        set => recoveryImportState.RemoveDirectorySyncCheckpoint =
            value ?? (() => { });
    }
    internal Func<string, IEnumerable<string>> RecoveryAliasEnumerateEntries
        { get; set; } = Directory.EnumerateFileSystemEntries;
    private readonly CancellationTokenSource blockCandidateShutdown = new();
    private int blockCandidateShutdownStarted;
    internal TimeSpan ShutdownDatabaseAttemptTimeout { get; set; } =
        TimeSpan.FromSeconds(5);
    internal TimeSpan ShutdownPersistenceDrainTimeout { get; set; } =
        TimeSpan.FromSeconds(20);
    internal TimeSpan ShutdownRecoveryCompletionTimeout { get; set; } =
        TimeSpan.FromSeconds(15);
    internal int PersistenceQueueCapacity { get; set; } = 65_536;
    internal int EmergencyJournalQueueCapacity { get; set; } = 1_024;
    internal int PersistenceQueueDepth =>
        Volatile.Read(ref persistenceQueueAccounting)?.Depth ?? 0;
    internal int PersistenceQueueHighWatermark =>
        Volatile.Read(ref persistenceQueueAccounting)?.HighWatermark ?? 0;
    internal long PersistenceQueueOverflowCount =>
        Volatile.Read(ref persistenceQueueAccounting)?.OverflowCount ?? 0;
    internal int EmergencyJournalQueueDepth =>
        Volatile.Read(ref emergencyJournalQueueAccounting)?.Depth ?? 0;
    internal int EmergencyJournalQueueHighWatermark =>
        Volatile.Read(ref emergencyJournalQueueAccounting)?.HighWatermark ?? 0;
    internal long EmergencyJournalQueueOverflowCount =>
        Volatile.Read(ref emergencyJournalQueueAccounting)?.OverflowCount ?? 0;
    SharePersistenceQueueMetricsSnapshot
        ISharePersistenceQueueMetricsProvider.GetPersistenceQueueMetrics() =>
        Volatile.Read(ref persistenceQueueAccounting)?.GetSnapshot() ??
        new SharePersistenceQueueMetricsSnapshot(0, 0,
            PersistenceQueueCapacity, 0);
    SharePersistenceQueueMetricsSnapshot
        ISharePersistenceQueueMetricsProvider.GetEmergencyJournalQueueMetrics() =>
        Volatile.Read(ref emergencyJournalQueueAccounting)?.GetSnapshot() ??
        new SharePersistenceQueueMetricsSnapshot(0, 0,
            EmergencyJournalQueueCapacity, 0);
    private BoundedQueueAccounting<QueuedShare> persistenceQueueAccounting;
    private BoundedQueueAccounting<QueuedShare> emergencyJournalQueueAccounting;
    private IDisposable shareSubscription;
    private ChannelWriter<QueuedShare> persistenceQueueWriter;
    private ChannelWriter<QueuedShare> emergencyJournalQueueWriter;
    private readonly CancellationTokenSource persistenceDrainCancellation = new();
    private readonly ConcurrentDictionary<long, QueuedShare> unresolvedShares = new();
    private readonly object deferredFailStopGate = new();
    private readonly List<Task> deferredFailStopHandling = new();
    private int recorderRecoveryOwnershipHeld;
    private long nextQueuedShareId;
    internal SemaphoreSlim RecoveryWriteGate => recoveryWriteState.Gate;
    internal long RecoveryValidationBytesRead =>
        Interlocked.Read(ref recoveryValidationBytesRead);
    private long recoveryValidationBytesRead;
    internal Action<string> RecoveryDirectorySync { get; set; }
    internal Action<string, string> RecoveryArchiveMove { get; set; }
    internal Action RecoveryArchiveMoveCheckpoint { get; set; } = () => { };
    internal Func<Stream, Task> RecoveryJournalFlush { get; set; } =
        FlushRecoveryJournalAsync;

    internal static void ForgetRecoveryWriteStateForTests(string filename)
    {
        RecoveryWriteStates.TryRemove(Path.GetFullPath(filename), out _);
    }
    private static readonly HashSet<string> UncertainBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auxpow-claim",
        "parent-uncertain",
        "merged-parent-uncertain",
    };

    internal async Task PersistSharesAsync(IList<Share> shares,
        CancellationToken ct = default)
    {
        var context = new Dictionary<string, object> { { PolicyContextKeyShares, shares } };

        await faultPolicy.ExecuteAsync((ctx, token) =>
                PersistSharesCoreAsync((IList<Share>) ctx[PolicyContextKeyShares], token),
            context, ct);
    }

    public Task PersistBlockCandidateAsync(Share share)
    {
        ArgumentNullException.ThrowIfNull(share);

        if(!share.BlockOnly || !share.IsBlockCandidate)
            throw new ArgumentException(
                "Synchronous block persistence requires a block-only candidate", nameof(share));

        // Shutdown may stop awaiting an in-flight insert and journal this candidate while the
        // original PostgreSQL operation later commits. Only candidate types whose stable identity
        // is backed by a matching unique index and conflict clause are safe on that path.
        BlockOnlyCandidatePersistenceRules.EnsureDeclared(share);

        return PersistBlockCandidateDurablyAsync(new[] { share });
    }

    public void BeginShutdown()
    {
        if(Interlocked.Exchange(ref blockCandidateShutdownStarted, 1) == 0)
            blockCandidateShutdown.Cancel();
    }

    private bool IsBlockCandidateShutdown =>
        Volatile.Read(ref blockCandidateShutdownStarted) != 0;

    private async Task PersistBlockCandidateDurablyAsync(IList<Share> shares)
    {
        Exception lastError = null;
        var unexpectedDatabaseFailure = false;

        for(var attempt = 0; attempt <= RetryCount; attempt++)
        {
            var databaseAttempt = PersistSharesCoreAsync(shares);

            try
            {
                await AwaitCandidateDatabaseAttemptAsync(databaseAttempt);
                return;
            }
            catch(TransactionCommittedCleanupException cleanupError)
            {
                await recoveryFailureHandler.StopClusterAfterCommittedCleanupAsync(
                    shares.ToArray(), recoveryFilename, cleanupError);
                throw;
            }
            catch(TransactionCommitOutcomeUncertainException commitError)
            {
                await recoveryFailureHandler.StopClusterForUncertainCommitAsync(
                    shares.ToArray(), recoveryFilename, commitError);
                throw;
            }
            catch(Exception ex) when(IsRetryablePersistenceException(ex))
            {
                lastError = ex;

                if(ex is TimeoutException && IsBlockCandidateShutdown &&
                    !databaseAttempt.IsCompleted)
                    _ = ObserveLateCandidateDatabaseAttemptAsync(databaseAttempt);
            }
            catch(Exception ex)
            {
                // A candidate is financially significant regardless of the exception type.
                // Preserve it in the journal even when the database failure falls outside the
                // normal retry policy, then stop the cluster because the pipeline is unhealthy.
                lastError = ex;
                unexpectedDatabaseFailure = true;
                break;
            }

            // Once shutdown starts, do not spend another fourteen seconds in retry delays.
            // The current database attempt was already given its bounded grace period; move
            // directly to the forced recovery-journal flush.
            if(IsBlockCandidateShutdown || attempt == RetryCount)
                break;

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            OnPolicyRetry(lastError, delay, attempt + 1, null);

            try
            {
                await Task.Delay(delay, blockCandidateShutdown.Token);
            }
            catch(OperationCanceledException) when(IsBlockCandidateShutdown)
            {
                break;
            }
        }

        Exception journalError = null;

        try
        {
            await WriteRecoveryJournalAsync(shares);
            NotifyAdminOnPolicyFallbackSafely();
        }
        catch(Exception ex)
        {
            journalError = ex;

            if(!hasLoggedPolicyFallbackFailure)
            {
                logger.Fatal(ex, "Fatal error during candidate recovery fallback. Block candidate will be lost!");
                hasLoggedPolicyFallbackFailure = true;
            }
        }

        if(journalError != null)
        {
            await candidateFailureHandler.StopClusterAsync(shares.ToArray(), lastError,
                journalError, false);

            RethrowCandidatePersistenceFailure(lastError, journalError);
        }

        if(unexpectedDatabaseFailure)
        {
            await candidateFailureHandler.StopClusterAsync(shares.ToArray(), lastError,
                null, true);

            RethrowCandidatePersistenceFailure(lastError, null);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void RethrowCandidatePersistenceFailure(Exception databaseError,
        Exception journalError)
    {
        var primary = databaseError ?? journalError ??
            new IOException("Unknown block-candidate persistence failure");

        if(databaseError != null && journalError != null)
            databaseError.Data["RecoveryJournalException"] = journalError;

        ExceptionDispatchInfo.Capture(primary).Throw();
        throw new InvalidOperationException("Unreachable candidate-persistence path");
    }

    private async Task AwaitCandidateDatabaseAttemptAsync(Task databaseAttempt)
    {
        if(!IsBlockCandidateShutdown)
        {
            try
            {
                await databaseAttempt.WaitAsync(blockCandidateShutdown.Token);
                return;
            }
            catch(OperationCanceledException) when(IsBlockCandidateShutdown)
            {
                // Shutdown owns a separate bounded database grace period below. Cancelling this
                // wait does not cancel the database command or its transaction.
            }
        }

        await databaseAttempt.WaitAsync(ShutdownDatabaseAttemptTimeout);
    }

    private static bool IsRetryablePersistenceException(Exception ex) =>
        ex is DbException or SocketException or TimeoutException or BrokenCircuitException;

    private static async Task ObserveLateCandidateDatabaseAttemptAsync(Task databaseAttempt)
    {
        try
        {
            await databaseAttempt;
        }
        catch(Exception ex)
        {
            logger.Warn(ex, () => "Late PostgreSQL candidate attempt failed after recovery-journal fallback");
        }
    }

    internal Task PersistSharesCoreAsync(IList<Share> shares) =>
        PersistSharesCoreAsync(shares, CancellationToken.None);

    private async Task PersistSharesCoreAsync(IList<Share> shares,
        CancellationToken ct)
    {
        var insertedBlocks = await cf.RunTx((con, tx) =>
            PersistSharesBatchAsync(con, tx, shares, ct), ct: ct,
            classifyCommitOutcome: true);

        NotifyPersistedBlocks(insertedBlocks);
    }

    private async Task<List<(string PoolId, Block Block)>> PersistSharesBatchAsync(
        IDbConnection con, IDbTransaction tx, IList<Share> shares,
        CancellationToken ct = default)
    {
        var result = new List<(string PoolId, Block Block)>();

        // Block-only candidates are sent through the share message pipeline so they can reuse
        // normal block persistence and notifications without creating a duplicate standalone
        // share row or distorting hashrate, effort, and share statistics.
        var mapped = GetSharesForPersistence(shares)
            .Select(mapper.Map<Persistence.Model.Share>)
            .ToArray();

        if(mapped.Length > 0)
            await shareRepo.BatchInsertAsync(con, tx, mapped, ct);

        // Insert blocks
        foreach(var share in shares)
        {
            if(!share.IsBlockCandidate || share.BlockRecordEmitted)
                continue;

            var blockEntity = mapper.Map<Block>(share);
            blockEntity.Status = BlockStatus.Pending;
            var inserted = await blockRepo.InsertAsync(con, tx, blockEntity, ct);

            if(!inserted)
                continue;

            if(IsUncertainBlockType(blockEntity.Type))
                continue;

            result.Add((share.PoolId, blockEntity));
        }

        return result;
    }

    private void NotifyPersistedBlocks(
        IEnumerable<(string PoolId, Block Block)> insertedBlocks)
    {
        foreach(var (poolId, block) in insertedBlocks)
        {
            try
            {
                if(pools.TryGetValue(poolId, out var poolConfig) &&
                    poolConfig.Template != null)
                    messageBus.NotifyBlockFound(poolId, block, poolConfig.Template);
                else if(poolConfig != null)
                    logger.Warn(() =>
                        $"Block-found notification skipped for pool {poolId} because its coin template is unavailable");
                else
                    logger.Warn(()=> $"Block found for unknown pool {poolId}");
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"Unable to emit block-found notification for pool {poolId}, block {block.BlockHeight} [{block.Hash}] after persistence committed");
            }
        }
    }

    internal static bool IsUncertainBlockType(string type)
    {
        return !string.IsNullOrEmpty(type) && UncertainBlockTypes.Contains(type);
    }

    internal static IEnumerable<Share> GetSharesForPersistence(IEnumerable<Share> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);

        return shares.Where(x => !x.BlockOnly);
    }

    private static void OnPolicyRetry(Exception ex, TimeSpan timeSpan, int retry, object context)
    {
        logger.Warn(() => $"Retry {retry} in {timeSpan} due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
    }

    private Task OnPolicyFallbackAsync(Exception ex, Context context)
    {
        context[PolicyContextKeyDatabaseError] = ex;
        logger.Warn(() => $"Fallback due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
        return Task.CompletedTask;
    }

    private static Task OnBrokenCircuitFallbackAsync(Exception ex,
        Context context)
    {
        context[PolicyContextKeyDatabaseError] = ex;
        return Task.CompletedTask;
    }

    private async Task OnExecutePolicyFallbackAsync(Context context, CancellationToken ct)
    {
        var shares = (IList<Share>) context[PolicyContextKeyShares];
        var databaseError = context.TryGetValue(
            PolicyContextKeyDatabaseError, out var value)
            ? value as Exception
            : null;

        await ExecuteRecoveryFallbackAsync(shares, databaseError);
    }

    private async Task ExecuteRecoveryFallbackAsync(IList<Share> shares,
        Exception databaseError)
    {
        ArgumentNullException.ThrowIfNull(shares);

        try
        {
            await WriteRecoveryJournalAsync(shares);
            NotifyAdminOnPolicyFallbackSafely();
        }

        catch(Exception ex)
        {
            await recoveryFailureHandler.StopClusterAsync(shares.ToArray(),
                recoveryFilename, databaseError, ex);

            var causes = databaseError != null
                ? new[] { databaseError, ex }
                : new[] { ex };
            var failure = new IOException(
                "Unable to durably persist shares to PostgreSQL or the recovery journal",
                new AggregateException(causes));

            throw failure;
        }
    }

    internal async Task WriteRecoveryJournalAsync(IList<Share> shares)
    {
        await recoveryWriteState.Gate.WaitAsync(CancellationToken.None);

        try
        {
            if(!recoveryPathOwnership.IsHeld)
                throw new InvalidOperationException(
                    $"Recovery journal ownership is not held for {recoveryFilename}");

            recoveryPathOwnership.EnsureJournalPathIsExclusive();
            RecoveryJournalPathValidatedCheckpoint();
            recoveryImportState.EnsureNoOutstandingImport();
            FileStream stream;

            try
            {
                // The no-follow writer handle is itself validated as a single-name regular file.
                // This closes the inspection/open substitution window as well as retaining the
                // exclusive active-file boundary.
                stream = recoveryPathOwnership.OpenRecoveryEntry(recoveryFilename,
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                    FileOptions.Asynchronous | FileOptions.WriteThrough,
                    "Recovery journal");
            }
            catch(FileNotFoundException)
            {
                if(recoveryWriteState.IsTrusted)
                    throw new InvalidDataException(
                        $"Recovery journal {recoveryFilename} disappeared after its append tail was validated. " +
                        "Preserve the surrounding storage for reconciliation; refusing to create a replacement.");

                recoveryTerminalState.EnsureJournalMayBeMissing();
                await CreateRecoveryJournalAsync(shares);
                return;
            }

            await using(stream)
            {
                var identity = RecoveryJournalFileIdentity.Read(stream);
                RecoveryJournalTail tail;

                if(!recoveryWriteState.IsTrusted)
                {
                    EnsureRecoveryJournalNewlineBoundary(stream, recoveryFilename);
                    tail = ValidateRecoveryJournalDetailed(stream, recoveryFilename);
                    recoveryTerminalState.EnsureConsistent(tail.Sequence,
                        tail.FrameDigest, tail.IsChainedFormat);
                    Interlocked.Add(ref recoveryValidationBytesRead, stream.Length);
                    recoveryWriteState.Trust(identity, stream.Length, tail);
                }
                else
                {
                    recoveryWriteState.Verify(identity, stream.Length,
                        recoveryFilename);
                    EnsureRecoveryJournalNewlineBoundary(stream, recoveryFilename);
                    tail = recoveryWriteState.Tail;
                }

                var payload = BuildRecoveryJournalPayload(shares,
                    stream.Length == 0, tail.Sequence + 1,
                    tail.FrameDigest);
                var originalLength = stream.Length;
                await AppendRecoveryJournalAsync(stream, payload.Bytes,
                    RecoveryJournalFlush);

                try
                {
                    RecoveryTerminalStateWrite(payload.Sequence,
                        payload.FrameDigest);
                }
                catch(Exception anchorError)
                {
                    try
                    {
                        stream.SetLength(originalLength);
                        stream.Position = originalLength;
                        await RecoveryJournalFlush(stream);
                    }
                    catch(Exception rollbackError)
                    {
                        throw new IOException(
                            "Recovery-journal terminal-anchor commit failed and the appended frame could not be rolled back",
                            new AggregateException(anchorError, rollbackError));
                    }

                    ExceptionDispatchInfo.Capture(anchorError).Throw();
                }

                // A Linux identity may include metadata populated by the completed append.
                // Refresh it only after the frame is force-flushed and therefore trusted.
                recoveryWriteState.Advance(RecoveryJournalFileIdentity.Read(stream),
                    stream.Length,
                    new RecoveryJournalTail(payload.Sequence,
                        payload.FrameDigest, true));
            }
        }
        finally
        {
            recoveryWriteState.Gate.Release();
        }
    }

    private async Task CreateRecoveryJournalAsync(IList<Share> shares)
    {
        var directory = Path.GetDirectoryName(recoveryFilename)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(recoveryFilename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            recoveryPathOwnership.EnsureJournalPathIsExclusive();
            await using(var stream = recoveryPathOwnership.OpenRecoveryEntry(
                temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.Asynchronous | FileOptions.WriteThrough,
                "Recovery journal temporary"))
            {
                var payload = BuildRecoveryJournalPayload(shares, true,
                    1, EmptyFrameDigest);
                await AppendRecoveryJournalAsync(stream, payload.Bytes,
                    RecoveryJournalFlush);
            }

            // A force-flushed file is not a durable first creation until its directory entry is
            // atomically published and the containing directory is synchronised on Linux.
            recoveryPathOwnership.EnsureJournalPathIsExclusive();
            MoveRecoveryEntry(recoveryPathOwnership, temporary, recoveryFilename);
            SyncRecoveryDirectory(recoveryPathOwnership, directory);
            recoveryPathOwnership.EnsureJournalPathIsExclusive();

            await using var active = recoveryPathOwnership.OpenRecoveryEntry(
                recoveryFilename, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, FileOptions.WriteThrough, "Recovery journal");
            var identity = RecoveryJournalFileIdentity.Read(active);
            var tail = ValidateRecoveryJournalDetailed(active, recoveryFilename);
            RecoveryTerminalStateWrite(tail.Sequence, tail.FrameDigest);
            Interlocked.Add(ref recoveryValidationBytesRead, active.Length);
            recoveryWriteState.Trust(identity, active.Length, tail);
        }
        finally
        {
            try
            {
                recoveryPathOwnership.DeleteRecoveryEntry(temporary);
            }
            catch
            {
                // Preserve the original create, rename, or directory-sync exception. A stray
                // temporary file is not treated as the active recovery journal.
            }
        }
    }

    internal static void EnsureRecoveryJournalAppendBoundary(Stream stream,
        string filename)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if(!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException(
                "Recovery journal stream must be readable and seekable", nameof(stream));

        EnsureRecoveryJournalNewlineBoundary(stream, filename);
        ValidateRecoveryJournal(stream, filename);
    }

    internal static bool ValidateRecoveryJournal(Stream stream, string filename)
    {
        return ValidateRecoveryJournalDetailed(stream, filename).IsChainedFormat;
    }

    private static void EnsureRecoveryJournalNewlineBoundary(Stream stream,
        string filename)
    {
        if(stream.Length == 0)
            return;

        stream.Position = stream.Length - 1;

        if(stream.ReadByte() != '\n')
            throw new InvalidDataException(
                $"Recovery journal {filename} does not end at a newline boundary. " +
                "Preserve it for reconciliation; refusing to append to possibly truncated data.");
    }

    internal static RecoveryJournalTail ValidateRecoveryJournalDetailed(Stream stream,
        string filename)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if(!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException(
                "Recovery journal stream must be readable and seekable", nameof(stream));

        if(stream.Length == 0)
            return new RecoveryJournalTail(0, EmptyFrameDigest, true);

        stream.Position = 0;
        using var reader = new StreamReader(stream,
            new UTF8Encoding(false, true), leaveOpen: true);
        using var lines = new BoundedLineReader(reader,
            MaxRecoveryRecordLineLength, $"Recovery journal {filename}",
            " Preserve it for reconciliation.");

        try
        {
            var firstLine = lines.ReadLine();

            if(firstLine == null)
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains no complete record or header");

            if(string.Equals(firstLine, RecoveryJournalMagic,
                   StringComparison.Ordinal))
            {
                ValidateRecoveryHeader(lines, filename, null);
                return ValidateRecoveryV2Frames(lines, filename,
                    lines.ReadLine(), EmptyFrameDigest, requireBatch: true);
            }

            using var legacyHash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            var isV1Journal = string.Equals(firstLine,
                RecoveryJournalMagicV1, StringComparison.Ordinal);

            if(isV1Journal)
            {
                AppendNormalizedLine(legacyHash, firstLine);
                ValidateRecoveryHeader(lines, filename, legacyHash);
                firstLine = lines.ReadLine();
            }

            return ValidateLegacyAndChainedFrames(lines, filename,
                firstLine, legacyHash, allowUnframedPrefix: !isV1Journal,
                requireV1Batch: isV1Journal);
        }
        catch(DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Recovery journal {filename} contains invalid UTF-8", ex);
        }
    }

    private static void ValidateRecoveryHeader(BoundedLineReader lines,
        string filename, IncrementalHash legacyHash)
    {
        var expectedHeader = new[]
        {
            "# The existence of this file means shares could not be committed to the database.",
            "# You should stop the pool cluster and run the following command:",
            "# miningcore -c <path-to-config> -rs <path-to-this-file>",
            string.Empty,
        };

        foreach(var expectedLine in expectedHeader)
        {
            var actualLine = lines.ReadLine();

            if(!string.Equals(actualLine, expectedLine,
                   StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains an incomplete or unexpected " +
                    "versioned header. Preserve it for reconciliation.");

            if(legacyHash != null)
                AppendNormalizedLine(legacyHash, actualLine);
        }
    }

    private static RecoveryJournalTail ValidateLegacyAndChainedFrames(
        BoundedLineReader lines, string filename, string firstLine,
        IncrementalHash legacyHash, bool allowUnframedPrefix,
        bool requireV1Batch)
    {
        var sawV1Batch = false;
        var line = firstLine;

        while(line != null)
        {
            if(line.StartsWith(RecoveryBatchV2StartPrefix,
                   StringComparison.Ordinal))
            {
                var legacyDigest = Convert.ToHexString(
                    legacyHash.GetHashAndReset());
                return ValidateRecoveryV2Frames(lines, filename, line,
                    legacyDigest, requireBatch: true);
            }

            AppendNormalizedLine(legacyHash, line);

            if(line.StartsWith(RecoveryBatchV1StartPrefix,
                   StringComparison.Ordinal))
            {
                sawV1Batch = true;
                ValidateRecoveryV1Frame(lines, filename, line, legacyHash);
            }
            else if(IsRecoveryFrameMarker(line) ||
                    !allowUnframedPrefix || sawV1Batch)
            {
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains unexpected content outside a " +
                    "framed batch. Preserve it for reconciliation.");
            }

            line = lines.ReadLine();
        }

        if(requireV1Batch && !sawV1Batch)
            throw new InvalidDataException(
                $"Recovery journal {filename} identifies the versioned v1 format but " +
                "contains no complete framed batch. Preserve it for reconciliation.");

        return new RecoveryJournalTail(0,
            Convert.ToHexString(legacyHash.GetHashAndReset()), false);
    }

    private static void ValidateRecoveryV1Frame(BoundedLineReader lines,
        string filename, string startLine, IncrementalHash legacyHash)
    {
        var startMetadata = ParseRecoveryV1Metadata(startLine,
            RecoveryBatchV1StartPrefix, filename);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;

        while(true)
        {
            var line = lines.ReadLine();

            if(line == null)
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains an incomplete framed batch (v1). " +
                    "Preserve it for reconciliation.");

            AppendNormalizedLine(legacyHash, line);

            if(line.StartsWith(RecoveryBatchV1StartPrefix,
                   StringComparison.Ordinal) ||
               line.StartsWith(RecoveryBatchV2StartPrefix,
                   StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains a nested framed batch. " +
                    "Preserve it for reconciliation.");

            if(line.StartsWith(RecoveryBatchV1EndPrefix,
                   StringComparison.Ordinal))
            {
                var endMetadata = ParseRecoveryV1Metadata(line,
                    RecoveryBatchV1EndPrefix, filename);
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());

                if(startMetadata != endMetadata || count != startMetadata.Count ||
                   !string.Equals(actualHash, startMetadata.Hash,
                       StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Recovery journal {filename} has a v1 frame whose record count " +
                        "or hash does not match its durable metadata. Preserve it for reconciliation.");

                return;
            }

            ValidateRecoveryRecordLine(line, filename);
            AppendNormalizedLine(hash, line);
            count++;
        }
    }

    private static RecoveryJournalTail ValidateRecoveryV2Frames(
        BoundedLineReader lines, string filename, string firstLine,
        string expectedPrevious, bool requireBatch)
    {
        var expectedSequence = 1L;
        var sawBatch = false;
        var line = firstLine;

        while(line != null)
        {
            if(!line.StartsWith(RecoveryBatchV2StartPrefix,
                   StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains unexpected content outside a " +
                    "chained frame. Preserve it for reconciliation.");

            sawBatch = true;
            var startMetadata = ParseRecoveryV2Metadata(line,
                RecoveryBatchV2StartPrefix, filename);

            if(startMetadata.Sequence != expectedSequence ||
               !string.Equals(startMetadata.Previous, expectedPrevious,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Recovery journal {filename} contains a missing, duplicate, or reordered " +
                    $"chained frame at sequence {startMetadata.Sequence}. Preserve it for reconciliation.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;

            while(true)
            {
                line = lines.ReadLine();

                if(line == null)
                    throw new InvalidDataException(
                        $"Recovery journal {filename} contains an incomplete chained frame. " +
                        "Preserve it for reconciliation.");

                if(line.StartsWith(RecoveryBatchV1StartPrefix,
                       StringComparison.Ordinal) ||
                   line.StartsWith(RecoveryBatchV2StartPrefix,
                       StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Recovery journal {filename} contains a nested framed batch. " +
                        "Preserve it for reconciliation.");

                if(line.StartsWith(RecoveryBatchV2EndPrefix,
                       StringComparison.Ordinal))
                {
                    var endMetadata = ParseRecoveryV2Metadata(line,
                        RecoveryBatchV2EndPrefix, filename);
                    var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                    var actualFrame = ComputeRecoveryFrameDigest(
                        startMetadata.Sequence, startMetadata.Previous,
                        startMetadata.Count, startMetadata.RecordHash);

                    if(startMetadata != endMetadata || count != startMetadata.Count ||
                       !string.Equals(actualHash, startMetadata.RecordHash,
                           StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals(actualFrame, startMetadata.FrameDigest,
                           StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Recovery journal {filename} has a framed batch whose record " +
                            "count, content hash, or chain digest does not match its durable metadata. Preserve it " +
                            "for reconciliation.");

                    expectedPrevious = startMetadata.FrameDigest;
                    expectedSequence++;
                    line = lines.ReadLine();
                    break;
                }

                ValidateRecoveryRecordLine(line, filename);
                AppendNormalizedLine(hash, line);
                count++;
            }
        }

        if(requireBatch && !sawBatch)
            throw new InvalidDataException(
                $"Recovery journal {filename} identifies the versioned format but contains " +
                "no complete framed batch. Preserve it for reconciliation.");

        return new RecoveryJournalTail(expectedSequence - 1,
            expectedPrevious, true);
    }

    private static bool IsRecoveryFrameMarker(string line) =>
        line?.StartsWith(RecoveryBatchV1StartPrefix, StringComparison.Ordinal) == true ||
        line?.StartsWith(RecoveryBatchV1EndPrefix, StringComparison.Ordinal) == true ||
        line?.StartsWith(RecoveryBatchV2StartPrefix, StringComparison.Ordinal) == true ||
        line?.StartsWith(RecoveryBatchV2EndPrefix, StringComparison.Ordinal) == true;

    private static (int Count, string Hash) ParseRecoveryV1Metadata(string line,
        string prefix, string filename)
    {
        var parts = line[prefix.Length..].Split(' ',
            StringSplitOptions.RemoveEmptyEntries);

        if(parts.Length != 2 ||
            !parts[0].StartsWith("count=", StringComparison.Ordinal) ||
            !int.TryParse(parts[0][6..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var count) || count < 0 ||
            !parts[1].StartsWith("sha256=", StringComparison.Ordinal) ||
            parts[1].Length != 71 ||
            !parts[1][7..].All(Uri.IsHexDigit))
            throw new InvalidDataException(
                $"Recovery journal {filename} contains malformed batch metadata");

        var hash = parts[1][7..];
        var canonical = $"{prefix}count={count} sha256={hash}";

        if(!string.Equals(line, canonical, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Recovery journal {filename} contains non-canonical batch metadata");

        return (count, hash);
    }

    private static RecoveryFrameMetadata ParseRecoveryV2Metadata(string line,
        string prefix, string filename)
    {
        var parts = line[prefix.Length..].Split(' ',
            StringSplitOptions.RemoveEmptyEntries);

        if(parts.Length != 5 ||
           !parts[0].StartsWith("sequence=", StringComparison.Ordinal) ||
           !long.TryParse(parts[0][9..], NumberStyles.None,
               CultureInfo.InvariantCulture, out var sequence) || sequence <= 0 ||
           !parts[1].StartsWith("previous=", StringComparison.Ordinal) ||
           !IsSha256(parts[1][9..]) ||
           !parts[2].StartsWith("count=", StringComparison.Ordinal) ||
           !int.TryParse(parts[2][6..], NumberStyles.None,
               CultureInfo.InvariantCulture, out var count) || count < 0 ||
           !parts[3].StartsWith("sha256=", StringComparison.Ordinal) ||
           !IsSha256(parts[3][7..]) ||
           !parts[4].StartsWith("frame=", StringComparison.Ordinal) ||
           !IsSha256(parts[4][6..]))
            throw new InvalidDataException(
                $"Recovery journal {filename} contains malformed chained-frame metadata");

        var metadata = new RecoveryFrameMetadata(sequence, parts[1][9..], count,
            parts[3][7..], parts[4][6..]);
        var canonical = prefix + FormatRecoveryV2Metadata(metadata);

        if(!string.Equals(line, canonical, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Recovery journal {filename} contains non-canonical chained-frame metadata");

        return metadata;
    }

    private static bool IsSha256(string value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static void ValidateRecoveryRecordLine(string line, string filename)
    {
        if(string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            throw new InvalidDataException(
                $"Recovery journal {filename} contains unexpected content inside a " +
                "framed batch. Preserve it for reconciliation.");
    }

    private static void AppendNormalizedLine(IncrementalHash hash, string line)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(line));
        hash.AppendData(new byte[] { (byte) '\n' });
    }

    private static string ComputeRecoveryFrameDigest(long sequence,
        string previous, int count, string recordHash)
    {
        var canonical = $"Miningcore recovery frame v2\nsequence={sequence}\n" +
            $"previous={previous}\ncount={count}\nsha256={recordHash}\n";
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
    }

    private static string FormatRecoveryV2Metadata(RecoveryFrameMetadata metadata) =>
        $"sequence={metadata.Sequence} previous={metadata.Previous} " +
        $"count={metadata.Count} sha256={metadata.RecordHash} " +
        $"frame={metadata.FrameDigest}";

    private RecoveryJournalPayload BuildRecoveryJournalPayload(
        IEnumerable<Share> shares, bool includeHeader, long sequence,
        string previous)
    {
        var shareRecords = shares
            .Select(share => JsonConvert.SerializeObject(share,
                jsonSerializerSettings))
            .ToArray();
        var records = shareRecords.Length > 0
            ? string.Join("\n", shareRecords) + "\n"
            : string.Empty;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(records)));
        var frameDigest = ComputeRecoveryFrameDigest(sequence, previous,
            shareRecords.Length, hash);
        var metadata = FormatRecoveryV2Metadata(new RecoveryFrameMetadata(
            sequence, previous, shareRecords.Length, hash, frameDigest));
        using var writer = new StringWriter(CultureInfo.InvariantCulture)
        {
            NewLine = "\n",
        };

        if(includeHeader)
        {
            // Format identity is the first durable content. Any first append torn after this
            // line is rejected instead of being mistaken for a valid legacy journal.
            writer.WriteLine(RecoveryJournalMagic);
            WriteRecoveryFileheader(writer);
        }

        writer.WriteLine(RecoveryBatchV2StartPrefix + metadata);
        writer.Write(records);
        writer.WriteLine(RecoveryBatchV2EndPrefix + metadata);

        return new RecoveryJournalPayload(
            new UTF8Encoding(false).GetBytes(writer.ToString()),
            sequence, frameDigest);
    }

    internal static async Task AppendRecoveryJournalAsync(Stream stream,
        ReadOnlyMemory<byte> payload)
    {
        await AppendRecoveryJournalAsync(stream, payload,
            FlushRecoveryJournalAsync);
    }

    internal static async Task AppendRecoveryJournalAsync(Stream stream,
        ReadOnlyMemory<byte> payload, Func<Stream, Task> flush)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(flush);

        if(!stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException(
                "Recovery journal stream must be writable and seekable", nameof(stream));

        var originalLength = stream.Length;
        stream.Position = originalLength;

        try
        {
            await stream.WriteAsync(payload, CancellationToken.None);
            await flush(stream);
        }
        catch(Exception writeError)
        {
            try
            {
                stream.SetLength(originalLength);
                stream.Position = originalLength;
                await flush(stream);
            }
            catch(Exception rollbackError)
            {
                throw new IOException(
                    "Recovery journal append failed and its partial write could not be rolled back",
                    new AggregateException(writeError, rollbackError));
            }

            ExceptionDispatchInfo.Capture(writeError).Throw();
        }
    }

    private static async Task FlushRecoveryJournalAsync(Stream stream)
    {
        await stream.FlushAsync(CancellationToken.None);

        if(stream is FileStream fileStream)
            fileStream.Flush(true);
        else
            stream.Flush();
    }

    private readonly record struct RecoveryFrameMetadata(long Sequence,
        string Previous, int Count, string RecordHash, string FrameDigest);

    private readonly record struct RecoveryJournalPayload(byte[] Bytes,
        long Sequence, string FrameDigest);

    internal readonly record struct RecoveryJournalTail(long Sequence,
        string FrameDigest, bool IsChainedFormat);

    private sealed class RecoveryJournalWriteState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool IsTrusted { get; private set; }
        public RecoveryJournalTail Tail { get; private set; }

        private RecoveryJournalFileIdentity identity;
        private long length;

        public void Trust(RecoveryJournalFileIdentity fileIdentity,
            long fileLength, RecoveryJournalTail tail)
        {
            identity = fileIdentity;
            length = fileLength;
            Tail = tail;
            IsTrusted = true;
        }

        public void Verify(RecoveryJournalFileIdentity fileIdentity,
            long fileLength, string filename)
        {
            if(identity != fileIdentity || length != fileLength)
                throw new InvalidDataException(
                    $"Recovery journal {filename} changed outside Miningcore after its " +
                    "append tail was validated. Preserve it for reconciliation; refusing to append.");
        }

        public void Advance(RecoveryJournalFileIdentity fileIdentity,
            long fileLength, RecoveryJournalTail tail)
        {
            Trust(fileIdentity, fileLength, tail);
        }
    }

    private static void WriteRecoveryFileheader(TextWriter writer)
    {
        writer.WriteLine("# The existence of this file means shares could not be committed to the database.");
        writer.WriteLine("# You should stop the pool cluster and run the following command:");
        writer.WriteLine("# miningcore -c <path-to-config> -rs <path-to-this-file>\n");
    }

    private sealed class MissingShareRecoveryFailureHandler :
        IShareRecoveryFailureHandler
    {
        public static readonly MissingShareRecoveryFailureHandler Instance = new();

        public Task StopClusterAsync(IReadOnlyCollection<Share> shares,
            string recoveryFilename, Exception databaseError, Exception journalError) =>
            throw new InvalidOperationException(
                "A required share-recovery failure handler was not supplied");

        public Task StopClusterAfterJournalAsync(
            IReadOnlyCollection<Share> shares, string recoveryFilename,
            Exception pipelineError) =>
            throw new InvalidOperationException(
                "A required share-recovery failure handler was not supplied");

        public Task StopClusterAfterCommittedCleanupAsync(
            IReadOnlyCollection<Share> shares, string recoveryFilename,
            Exception cleanupError) =>
            throw new InvalidOperationException(
                "A required share-recovery failure handler was not supplied");

        public Task StopClusterForUncertainCommitAsync(
            IReadOnlyCollection<Share> shares, string recoveryFilename,
            Exception commitError) =>
            throw new InvalidOperationException(
                "A required share-recovery failure handler was not supplied");
    }

    private sealed class MissingMiningFailStopCoordinator :
        IMiningFailStopCoordinator
    {
        public static readonly MissingMiningFailStopCoordinator Instance = new();
        public bool IsFailStopRequested => false;
        public CancellationToken Token => CancellationToken.None;
        public IMiningSubmissionAcceptance AcquireSubmissionAcceptance() =>
            throw new InvalidOperationException(
                "The test-only ShareRecorder constructor has no mining admission coordinator");
        public bool BeginFailStop(int exitCode) => false;
    }

    private sealed class TestShareRecoveryPathOwnership :
        IShareRecoveryPathOwnership
    {
        public TestShareRecoveryPathOwnership(string recoveryFilename)
        {
            inner = new ShareRecoveryPathOwnership(recoveryFilename);
        }

        private readonly ShareRecoveryPathOwnership inner;
        public string RecoveryFilename => inner.RecoveryFilename;
        public string OwnershipFilename => inner.OwnershipFilename;
        // Direct journal unit tests use compatibility constructors without a hosted-service
        // lifecycle. Production paths and explicit ownership tests inject the real owner.
        public bool IsHeld => true;
        public void Acquire() => inner.Acquire();
        public void EnsureJournalPathIsExclusive()
        {
            if(inner.IsHeld)
                inner.EnsureJournalPathIsExclusive();
            else
                RecoveryJournalPathSafety.EnsureSinglePhysicalNameIfExists(
                    RecoveryFilename);
        }
        public FileStream OpenRecoveryEntry(string filename, FileMode mode,
            FileAccess access, FileShare share, FileOptions options,
            string description)
        {
            if(inner.IsHeld)
                return inner.OpenRecoveryEntry(filename, mode, access, share,
                    options, description);
            return RecoveryJournalPathSafety.OpenRegularFileNoFollow(filename,
                mode, access, share, options, description);
        }
        public FileStream TryOpenRecoveryEntry(string filename,
            FileAccess access, FileShare share, FileOptions options,
            string description)
        {
            if(inner.IsHeld)
                return inner.TryOpenRecoveryEntry(filename, access, share,
                    options, description);
            try
            {
                return RecoveryJournalPathSafety.OpenRegularFileNoFollow(filename,
                    FileMode.Open, access, share, options, description);
            }
            catch(FileNotFoundException)
            {
                return null;
            }
        }
        public void MoveRecoveryEntry(string sourceFilename,
            string destinationFilename)
        {
            if(inner.IsHeld)
            {
                inner.MoveRecoveryEntry(sourceFilename, destinationFilename);
                return;
            }
            File.Move(sourceFilename, destinationFilename, false);
        }
        public void DeleteRecoveryEntry(string filename)
        {
            if(inner.IsHeld)
            {
                inner.DeleteRecoveryEntry(filename);
                return;
            }
            File.Delete(filename);
        }
        public void SyncRecoveryDirectory()
        {
            if(inner.IsHeld)
            {
                inner.SyncRecoveryDirectory();
                return;
            }
            ShareRecoveryFatalState.SyncDirectoryWhereSupported(
                Path.GetDirectoryName(RecoveryFilename)!);
        }
        public void Release() => inner.Release();
        public void Dispose() => inner.Dispose();
    }

    private void MoveRecoveryEntry(IShareRecoveryPathOwnership ownership,
        string source, string destination)
    {
        if(RecoveryArchiveMove != null)
            RecoveryArchiveMove(source, destination);
        else
            ownership.MoveRecoveryEntry(source, destination);
    }

    private void SyncRecoveryDirectory(IShareRecoveryPathOwnership ownership,
        string directory)
    {
        if(RecoveryDirectorySync != null)
            RecoveryDirectorySync(directory);
        else
            ownership.SyncRecoveryDirectory();
    }

    public async Task<string> RecoverSharesAsync(string filename)
    {
        filename = Path.GetFullPath(filename);
        logger.Info(() => $"Recovering shares using {filename} ...");
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var configuredSource = string.Equals(filename, recoveryFilename,
            comparison);
        var operationOwnership = configuredSource
            ? recoveryPathOwnership
            : new ShareRecoveryPathOwnership(filename);
        operationOwnership.Acquire();

        try
        {
            if(!configuredSource && RecoveryPathsReferToSameFile(filename,
                   recoveryFilename))
                throw new InvalidDataException(
                    $"Recovery source {filename} is a filesystem alias of the configured active " +
                    $"journal {recoveryFilename}. Recover the exact configured path so its terminal " +
                    "anchor, import marker and retirement operation cannot be bypassed.");
            var importState = configuredSource
                ? recoveryImportState
                : new ShareRecoveryImportState(filename,
                    ShareRecoveryFatalState.ResolveStateDirectory(clusterConfig));

            var existingMarker = importState.TryRead();
            if(existingMarker != null && existingMarker.Phase !=
               ShareRecoveryImportState.ImportPhase.Pending)
            {
                var resumedArchive = await RetireImportedRecoveryFileAsync(filename,
                    existingMarker, configuredSource, importState,
                    operationOwnership);
                logger.Info(() => $"Completed durable retirement of previously imported " +
                    $"recovery source {filename} as {resumedArchive}");
                return resumedArchive;
            }

            List<(string PoolId, Block Block)> insertedBlocks = new();
            int validatedCount;
            string fileHash;
            ShareRecoveryImportState.ImportMarker marker;
            RecoveryJournalTail? configuredTail = null;
            var insertedNewContent = false;

            // Hold one read-only handle across both passes. FileShare.Read permits diagnostics
            // and backups, but prevents the recovery source from being changed between validation
            // and import.
            await using(var stream = operationOwnership.OpenRecoveryEntry(filename,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan,
                "Recovery import source"))
            {
                operationOwnership.EnsureJournalPathIsExclusive();
                // Enforce every count/hash frame before opening the database transaction. The
                // same read-only handle remains locked for both semantic passes, preventing the
                // source from changing after its byte-level integrity was established.
                EnsureRecoveryJournalAppendBoundary(stream, filename);
                if(configuredSource)
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    var tail = ValidateRecoveryJournalDetailed(stream, filename);
                    recoveryTerminalState.EnsureConsistent(tail.Sequence,
                        tail.FrameDigest, tail.IsChainedFormat);
                    configuredTail = tail;
                }
                stream.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));

                // Pass one validates every record before a database transaction is opened.
                var validationHash = new RecoveryContentHasher(jsonSerializerSettings);
                var requiresBlockIdempotencyIndexes = false;
                validatedCount = await ProcessRecoveryRecordsAsync(reader, shares =>
                {
                    validationHash.Append(shares);
                    requiresBlockIdempotencyIndexes |= shares.Any(
                        BlockOnlyCandidatePersistenceRules.RequiresIdempotencyIndexes);
                    return Task.CompletedTask;
                });
                fileHash = validationHash.GetHash();
                operationOwnership.EnsureJournalPathIsExclusive();

                // Recovery deliberately strips live merged-mining settings, so the validated
                // evidence—not current deployment configuration—decides whether these indexes
                // are required. Check before publishing the pending marker or opening the import
                // transaction, while the locked source handle still pins the validated journal.
                if(requiresBlockIdempotencyIndexes)
                {
                    var schemaReady = await cf.Run(con =>
                        blockRepo.HasMergedMiningBlockIndexesAsync(con,
                            CancellationToken.None));
                    if(!schemaReady)
                        throw new PoolStartupException(
                            BlockOnlyCandidatePersistenceRules.MissingIndexesMessage);

                    operationOwnership.EnsureJournalPathIsExclusive();
                }

                // Publish an independent pending marker before the database transaction begins.
                // A crash after commit but before source retirement can therefore be resumed
                // without allowing normal mining to append to the already-imported source.
                var reservedArchiveFilename = existingMarker?.ArchiveFilename ??
                    BuildRecoveryArchiveFilename(filename);
                marker = importState.Begin(fileHash, validatedCount,
                    reservedArchiveFilename, configuredTail?.Sequence,
                    configuredTail?.FrameDigest,
                    configuredSource && configuredTail?.IsChainedFormat == true);

                reader.DiscardBufferedData();
                stream.Seek(0, SeekOrigin.Begin);

                if(marker.Phase == ShareRecoveryImportState.ImportPhase.Pending)
                {
                    // Pass two imports every batch through one transaction. A retained pending
                    // marker plus an existing manifest means a previous attempt committed before
                    // it could advance the marker; retire the source without inserting it again.
                    operationOwnership.EnsureJournalPathIsExclusive();
                    var importResult = await cf.RunTx(async (con, tx) =>
                    {
                        var registered = await shareRepo.TryRegisterRecoveryImportAsync(con,
                            tx, fileHash, Path.GetFileName(filename), validatedCount,
                            CancellationToken.None);

                        if(!registered)
                            return (Blocks: new List<(string PoolId, Block Block)>(),
                                Inserted: false);

                        var result = new List<(string PoolId, Block Block)>();
                        var importHash = new RecoveryContentHasher(jsonSerializerSettings);
                        var importedCount = await ProcessRecoveryRecordsAsync(reader,
                            async shares =>
                            {
                                importHash.Append(shares);
                                result.AddRange(await PersistSharesBatchAsync(con, tx, shares));
                            });
                        var importedHash = importHash.GetHash();

                        if(importedCount != validatedCount ||
                           !string.Equals(importedHash, fileHash,
                               StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                "Recovery source changed between validation and import");

                        return (Blocks: result, Inserted: true);
                    });
                    insertedBlocks = importResult.Blocks;
                    insertedNewContent = importResult.Inserted;

                    // This write occurs only after RunTx commits. If the process dies between the
                    // commit and this update, the pending marker remains and the manifest makes
                    // the next recovery attempt classify the source as already committed.
                    marker = importState.MarkCommitted(marker);
                }
            }

            var archiveFilename = await RetireImportedRecoveryFileAsync(filename, marker,
                configuredSource, importState, operationOwnership);
            NotifyPersistedBlocks(insertedBlocks);
            logger.Info(() => insertedNewContent
                ? $"Successfully imported {validatedCount} shares and durably archived the " +
                  $"source as {archiveFilename}"
                : $"Recovery content [{fileHash}] was already committed; durably archived " +
                  $"the retained source as {archiveFilename} without replay");
            return archiveFilename;
        }

        catch(FileNotFoundException)
        {
            logger.Error(() => $"Recovery file {filename} was not found");
            throw;
        }
        finally
        {
            operationOwnership.Release();
            if(!ReferenceEquals(operationOwnership, recoveryPathOwnership))
                operationOwnership.Dispose();
        }
    }

    internal sealed class RecoveryContentHasher
    {
        public RecoveryContentHasher(JsonSerializerSettings serializerSettings)
        {
            this.serializerSettings = serializerSettings;
        }

        private const int AccumulatorCount = 4;
        private const int DigestSize = 32;
        private static readonly byte[] ManifestDomain =
            Encoding.ASCII.GetBytes("Miningcore recovery multiset v2");
        private readonly JsonSerializerSettings serializerSettings;
        private readonly byte[][] accumulatorSums = Enumerable.Range(0, AccumulatorCount)
            .Select(_ => new byte[DigestSize])
            .ToArray();

        internal ulong RecordCount { get; private set; }
        internal int AccumulatorStorageBytes => AccumulatorCount * DigestSize;

        public void Append(IEnumerable<Share> shares)
        {
            foreach(var share in shares)
            {
                var json = JsonConvert.SerializeObject(share, Formatting.None,
                    serializerSettings);
                AppendNormalizedRecord(Encoding.UTF8.GetBytes(json));
            }
        }

        internal void AppendNormalizedRecord(ReadOnlySpan<byte> record)
        {
            Span<byte> recordDigest = stackalloc byte[DigestSize];
            SHA256.HashData(record, recordDigest);

            Span<byte> domainInput = stackalloc byte[DigestSize + 1];
            recordDigest.CopyTo(domainInput[1..]);
            Span<byte> domainDigest = stackalloc byte[DigestSize];

            // Four independently domain-separated 256-bit additive accumulators form a
            // commutative multiset identity. Addition is modulo 2^256, so memory remains
            // constant while record order is ignored. The final cardinality prevents a
            // different duplicate count from sharing an otherwise equal accumulator state.
            for(var domain = 0; domain < AccumulatorCount; domain++)
            {
                domainInput[0] = (byte) domain;
                SHA256.HashData(domainInput, domainDigest);
                AddModulo256(accumulatorSums[domain], domainDigest);
            }

            RecordCount = checked(RecordCount + 1);
        }

        public string GetHash()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(ManifestDomain);

            Span<byte> count = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(count, RecordCount);
            hash.AppendData(count);

            foreach(var accumulator in accumulatorSums)
                hash.AppendData(accumulator);

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void AddModulo256(Span<byte> accumulator,
            ReadOnlySpan<byte> value)
        {
            var carry = 0;
            for(var i = DigestSize - 1; i >= 0; i--)
            {
                var sum = accumulator[i] + value[i] + carry;
                accumulator[i] = (byte) sum;
                carry = sum >> 8;
            }
        }
    }

    private static string BuildRecoveryArchiveFilename(string filename)
    {
        return $"{filename}.imported-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-" +
            Guid.NewGuid().ToString("N");
    }

    private bool RecoveryPathsReferToSameFile(string first,
        string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        first = Path.GetFullPath(first);
        second = Path.GetFullPath(second);

        if(string.Equals(first, second, comparison))
            return true;

        // Identity uncertainty must not downgrade a possible alias into an independent reviewed
        // source with different path-scoped state. Exact enumeration distinguishes proven absence
        // from access/I/O uncertainty, and no-follow handles prevent symlink substitution.
        using var firstStream = RecoveryStateFile.TryOpenExactEntry(first,
            RecoveryAliasEnumerateEntries);
        using var secondStream = RecoveryStateFile.TryOpenExactEntry(second,
            RecoveryAliasEnumerateEntries);

        if(firstStream == null || secondStream == null)
            return false;

        return RecoveryJournalFileIdentity.Read(firstStream) ==
            RecoveryJournalFileIdentity.Read(secondStream);
    }

    private async Task<RecoveryJournalTail> ValidateCommittedRecoveryFileAsync(FileStream stream,
        string filename, ShareRecoveryImportState.ImportMarker marker,
        bool configuredSource, bool requireAnchor)
    {
        EnsureRecoveryJournalAppendBoundary(stream, filename);
        stream.Seek(0, SeekOrigin.Begin);
        var tail = ValidateRecoveryJournalDetailed(stream, filename);

        if(configuredSource)
        {
            if(marker.TerminalSequence.HasValue)
                ShareRecoveryImportState.EnsureTerminalStateMatches(marker,
                    tail.Sequence, tail.FrameDigest, tail.IsChainedFormat);

            recoveryTerminalState.EnsureConsistent(tail.Sequence,
                tail.FrameDigest, requireAnchor && tail.IsChainedFormat);
        }

        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, new UTF8Encoding(false),
            true, 1024, leaveOpen: true);
        var contentHash = new RecoveryContentHasher(jsonSerializerSettings);
        var recordCount = await ProcessRecoveryRecordsAsync(reader, shares =>
        {
            contentHash.Append(shares);
            return Task.CompletedTask;
        });
        var fileHash = contentHash.GetHash();

        if(recordCount != marker.RecordCount ||
           !string.Equals(fileHash, marker.FileHash,
               StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Committed recovery source {filename} no longer matches its import marker. " +
                $"Expected {marker.RecordCount} records " +
                $"[{marker.FileHash}], found {recordCount} [{fileHash}]. Preserve all evidence " +
                "and reconcile the source before retirement.");

        return tail;
    }

    private async Task<string> RetireImportedRecoveryFileAsync(string filename,
        ShareRecoveryImportState.ImportMarker marker, bool configuredSource,
        ShareRecoveryImportState importState,
        IShareRecoveryPathOwnership operationOwnership)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if(marker.Phase == ShareRecoveryImportState.ImportPhase.Pending)
            throw new InvalidDataException(
                $"Recovery source {filename} cannot be retired before its database import is committed");

        operationOwnership.EnsureJournalPathIsExclusive();

        using(var manifestConnection = await cf.OpenConnectionAsync())
        {
            var manifestExists = await shareRepo.HasMatchingRecoveryImportAsync(
                manifestConnection, marker.FileHash, Path.GetFileName(filename),
                marker.RecordCount, CancellationToken.None);
            if(!manifestExists)
                throw new InvalidDataException(
                    $"PostgreSQL cannot prove the committed recovery import for {filename} " +
                    $"[{marker.FileHash}] with {marker.RecordCount} records. Preserve the source " +
                    $"and marker {importState.Filename}; refusing destructive retirement.");
        }

        using var source = operationOwnership.TryOpenRecoveryEntry(filename,
            FileAccess.Read, FileShare.Read | FileShare.Delete,
            FileOptions.SequentialScan, "Committed recovery source");
        using var archive = operationOwnership.TryOpenRecoveryEntry(
            marker.ArchiveFilename, FileAccess.Read,
            FileShare.Read | FileShare.Delete, FileOptions.SequentialScan,
            "Committed recovery archive");
        var sourceExists = source != null;
        var archiveExists = archive != null;

        if(sourceExists == archiveExists)
            throw new IOException(sourceExists
                ? $"Both recovery source {filename} and archive target " +
                  $"{marker.ArchiveFilename} exist"
                : $"Neither committed recovery source {filename} nor its recorded archive " +
                  $"{marker.ArchiveFilename} exists");

        var retainedFilename = sourceExists ? filename : marker.ArchiveFilename;
        var retained = source ?? archive;
        var validatedIdentity = RecoveryJournalFileIdentity.ReadStable(retained);
        var anchorMustRemain = marker.Phase <
            ShareRecoveryImportState.ImportPhase.AnchorRetirementAuthorised;
        var validatedTail = await ValidateCommittedRecoveryFileAsync(retained,
            retainedFilename, marker, configuredSource, anchorMustRemain);

        if(!marker.TerminalSequence.HasValue)
            marker = importState.RecordTerminalState(marker,
                validatedTail.Sequence, validatedTail.FrameDigest,
                configuredSource && validatedTail.IsChainedFormat);

        if(sourceExists)
        {
            RecoveryArchiveMoveCheckpoint();
            importState.EnsureCurrent(marker);
            operationOwnership.EnsureJournalPathIsExclusive();
            MoveRecoveryEntry(operationOwnership, filename,
                marker.ArchiveFilename);
            using var archived = operationOwnership.TryOpenRecoveryEntry(
                marker.ArchiveFilename, FileAccess.Read,
                FileShare.Read | FileShare.Delete, FileOptions.SequentialScan,
                "Committed recovery archive");
            if(archived == null)
                throw new IOException(
                    $"Recovery archive {marker.ArchiveFilename} disappeared after rename");
            var archivedIdentity = RecoveryJournalFileIdentity.ReadStable(archived);

            if(archivedIdentity != validatedIdentity)
                throw new InvalidDataException(
                    $"Recovery source {filename} was replaced while it was being retired. " +
                    $"The committed marker remains at {importState.Filename}; preserve the " +
                    "archive and surrounding storage for reconciliation.");

            operationOwnership.EnsureJournalPathIsExclusive();
        }

        // Re-read the still-open, non-writable file object after the rename. This catches a
        // same-inode modification between the initial check and retirement as well as a pathname
        // replacement (which is independently caught by the identity comparison above).
        await ValidateCommittedRecoveryFileAsync(retained,
            marker.ArchiveFilename, marker, configuredSource,
            anchorMustRemain);

        if(marker.Phase == ShareRecoveryImportState.ImportPhase.Committed)
        {
            // Persist the source rename before authorising retirement of its independent anchor.
            // If this sync fails, the committed marker remains and recovery repeats validation.
            SyncRecoveryDirectory(operationOwnership,
                Path.GetDirectoryName(filename)!);
            marker = importState.MarkArchiveDurable(marker);
        }

        if(marker.Phase == ShareRecoveryImportState.ImportPhase.ArchiveDurable)
            marker = importState.AuthoriseAnchorRetirement(marker);

        if(marker.Phase ==
           ShareRecoveryImportState.ImportPhase.AnchorRetirementAuthorised)
        {
            // The durable authorisation makes an already-absent anchor an idempotent completed
            // step on resume. Revalidate the archived content and recorded tail before removal.
            await ValidateCommittedRecoveryFileAsync(retained,
                marker.ArchiveFilename, marker, configuredSource, false);
            operationOwnership.EnsureJournalPathIsExclusive();
            importState.EnsureCurrent(marker);

            if(configuredSource && marker.TerminalAnchorRequired)
                RecoveryTerminalStateRemove();

            RecoveryAnchorRemovedCheckpoint();
            marker = importState.MarkAnchorRetired(marker);
        }

        // The anchor-retired marker is the last object removed. Its own directory sync makes
        // the completed retirement durable and prevents a stale marker from being resurrected.
        if(marker.Phase == ShareRecoveryImportState.ImportPhase.AnchorRetired)
        {
            importState.EnsureCurrent(marker);
            importState.RemoveAfterRetirement();
        }
        return marker.ArchiveFilename;
    }

    private async Task<int> ProcessRecoveryRecordsAsync(StreamReader reader,
        Func<IList<Share>, Task> processBatch)
    {
        const int bufferSize = 100;
        var shares = new List<Share>(bufferSize);
        var recordCount = 0;
        var lineNumber = 0;
        using var lines = new BoundedLineReader(reader,
            MaxRecoveryRecordLineLength,
            "Recovery journal recovery import source",
            " Preserve it for reconciliation.");

        while(true)
        {
            var line = lines.ReadLine();

            if(line == null)
                break;

            lineNumber++;

            if(string.IsNullOrWhiteSpace(line))
                continue;

            line = line.Trim();

            if(line.StartsWith("#"))
                continue;

            Share share;

            try
            {
                share = JsonConvert.DeserializeObject<Share>(line,
                    jsonSerializerSettings);
            }

            catch(JsonException ex)
            {
                throw new InvalidDataException(
                    $"Unable to parse recovery record at line {lineNumber}", ex);
            }

            if(share == null)
                throw new InvalidDataException(
                    $"Recovery record at line {lineNumber} is null");

            shares.Add(share);

            if(shares.Count < bufferSize)
                continue;

            await processBatch(shares);
            recordCount += shares.Count;
            shares.Clear();
        }

        if(shares.Count > 0)
        {
            await processBatch(shares);
            recordCount += shares.Count;
        }

        return recordCount;
    }

    private void NotifyAdminOnPolicyFallback()
    {
        if(clusterConfig.Notifications?.Admin?.Enabled == true &&
           clusterConfig.Notifications?.Admin?.NotifyPaymentSuccess == true &&
           !notifiedAdminOnPolicyFallback)
        {
            notifiedAdminOnPolicyFallback = true;

            messageBus.SendMessage(new AdminNotification("Share Recorder Policy Fallback",
                $"The Share Recorder's Policy Fallback has been engaged. Check share recovery file {recoveryFilename}."));
        }
    }

    private void NotifyAdminOnPolicyFallbackSafely()
    {
        try
        {
            NotifyAdminOnPolicyFallback();
        }
        catch(Exception ex)
        {
            logger.Error(ex,
                "Unable to emit share-recorder fallback notification after the recovery journal was durably flushed");
        }
    }

    private void BuildFaultHandlingPolicy()
    {
        // retry with increasing delay (1s, 2s, 4s etc)
        var retry = Policy
            .Handle<DbException>()
            .Or<SocketException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(RetryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                OnPolicyRetry);

        // after retries failed several times, break the circuit and fall through to
        // fallback action for one minute, not attempting further retries during that period
        var breaker = Policy
            .Handle<DbException>()
            .Or<SocketException>()
            .Or<TimeoutException>()
            .CircuitBreakerAsync(2, TimeSpan.FromMinutes(1));

        var fallback = Policy
            .Handle<DbException>()
            .Or<SocketException>()
            .Or<TimeoutException>()
            .FallbackAsync(OnExecutePolicyFallbackAsync, OnPolicyFallbackAsync);

        var fallbackOnBrokenCircuit = Policy
            .Handle<BrokenCircuitException>()
            .FallbackAsync(OnExecutePolicyFallbackAsync,
                OnBrokenCircuitFallbackAsync);

        faultPolicy = Policy.WrapAsync(
            fallbackOnBrokenCircuit,
            Policy.WrapAsync(fallback, breaker, retry));
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await StopCoreAsync(ct);
        }
        catch
        {
            // Unit fixtures run several intentional process-fatal paths inside one test host.
            // Production ownership is deliberately retained until process exit on these paths;
            // the compatibility constructors release only their test-scoped lock for cleanup.
            if(recoveryPathOwnership is TestShareRecoveryPathOwnership)
                ReleaseRecorderRecoveryOwnership();
            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        var retainOwnershipUntilProcessExit = false;
        try
        {
            BeginShutdown();
            Interlocked.Exchange(ref shareSubscription, null)?.Dispose();
            Volatile.Read(ref persistenceQueueWriter)?.TryComplete();
            Volatile.Read(ref emergencyJournalQueueWriter)?.TryComplete();

            // The caller token can end the database-drain phase early, but it must not cancel the
            // reserved recovery/evidence phase. Transaction recovery and deferred fatal handling
            // share one later deadline instead of each receiving a fresh 15-second allowance.
            using var recoveryCompletion = new CancellationTokenSource();
            var recoveryDeadlineStarted = false;
            using var databaseDrain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            databaseDrain.CancelAfter(ShutdownPersistenceDrainTimeout);

            try
            {
                if(ExecuteTask != null)
                    await ExecuteTask.WaitAsync(databaseDrain.Token);
            }
            catch(OperationCanceledException) when(databaseDrain.IsCancellationRequested)
            {
                // Reserve the remainder of the host/systemd shutdown window for accounting
                // recovery. The queue worker catches this cancellation and force-flushes its
                // complete unresolved registry to the journal before StopAsync may finish.
                persistenceDrainCancellation.Cancel();
                recoveryCompletion.CancelAfter(ShutdownRecoveryCompletionTimeout);
                recoveryDeadlineStarted = true;

                if(ExecuteTask != null)
                {
                    try
                    {
                        await ExecuteTask.WaitAsync(recoveryCompletion.Token);
                    }
                    catch(OperationCanceledException ex) when(
                        recoveryCompletion.IsCancellationRequested)
                    {
                        // WaitAsync cancelled only this wait. The persistence worker may still
                        // unwind into shutdown journalling or fatal-evidence publication, so keep
                        // its native recovery owner before awaiting any further failure handling.
                        retainOwnershipUntilProcessExit = true;
                        var unresolved = failStopCoordinator.BeginFailStopAndCapture(
                            ProcessExitCodes.UnreconciledShareDurabilityLoss,
                            () => SnapshotUnresolvedShares());
                        await recoveryFailureHandler.StopClusterForUncertainCommitAsync(
                            unresolved, recoveryFilename, new TimeoutException(
                                "The share-persistence transaction did not stop within the bounded " +
                                "post-cancellation recovery window; its database outcome is uncertain",
                                ex));
                        throw new TimeoutException(
                            "The share-persistence transaction exceeded the shared shutdown deadline",
                            ex);
                    }
                }
            }

            if(!recoveryDeadlineStarted)
                recoveryCompletion.CancelAfter(ShutdownRecoveryCompletionTimeout);

            await base.StopAsync(recoveryCompletion.Token);

            Task deferredHandling = null;
            lock(deferredFailStopGate)
            {
                if(deferredFailStopHandling.Count > 0)
                    deferredHandling = Task.WhenAll(deferredFailStopHandling.ToArray());
            }

            try
            {
                if(deferredHandling != null)
                    await deferredHandling.WaitAsync(recoveryCompletion.Token);
            }
            catch(OperationCanceledException ex) when(
                recoveryCompletion.IsCancellationRequested)
            {
                // The evidence task may still be mutating the retained recovery directory.
                // Releasing ownership here would permit a replacement process to race it, so
                // retain the native lock explicitly until this failed process exits.
                retainOwnershipUntilProcessExit = true;
                logger.Fatal(ex,
                    "Deferred share-recovery evidence did not finish within the shared shutdown deadline; retaining recovery ownership until process exit");
                throw new TimeoutException(
                    "Deferred share-recovery evidence exceeded the shared shutdown deadline", ex);
            }
            catch(Exception ex)
            {
                // A faulted task is complete and no longer mutating the directory. Log the evidence
                // failure, release the recorder-owned lease in finally, and preserve the stop failure.
                logger.Fatal(ex, "Deferred share-recovery evidence failed during shutdown");
                throw;
            }
        }
        finally
        {
            var persistenceWorkerComplete = ExecuteTask == null || ExecuteTask.IsCompleted;
            bool deferredRecoveryComplete;
            lock(deferredFailStopGate)
                deferredRecoveryComplete = deferredFailStopHandling.All(x => x.IsCompleted);

            // A cancelled wait does not cancel its underlying worker. Release a recorder-scoped
            // native owner only after every task capable of mutating recovery evidence is proven
            // complete; otherwise the failed process retains ownership until it exits.
            if(!retainOwnershipUntilProcessExit && persistenceWorkerComplete &&
               deferredRecoveryComplete)
                ReleaseRecorderRecoveryOwnership();
        }
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            AcquireRecorderRecoveryOwnership();
            logger.Info(() => $"Online; recovery journal {recoveryFilename}");
            var queue = Channel.CreateBounded<QueuedShare>(new BoundedChannelOptions(
                PersistenceQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            var emergencyQueue = Channel.CreateBounded<QueuedShare>(
                new BoundedChannelOptions(EmergencyJournalQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false,
                });
            var primaryAccounting = new BoundedQueueAccounting<QueuedShare>(
                PersistenceQueueCapacity);
            var emergencyAccounting = new BoundedQueueAccounting<QueuedShare>(
                EmergencyJournalQueueCapacity);
            Volatile.Write(ref persistenceQueueAccounting, primaryAccounting);
            Volatile.Write(ref emergencyJournalQueueAccounting,
                emergencyAccounting);
            var processing = ObservePersistenceQueuesAsync(
                RunPersistenceQueuesAsync(queue, emergencyQueue,
                    primaryAccounting, emergencyAccounting));

            SignalStartupReady();
            return processing;
        }

        catch(Exception ex)
        {
            SignalStartupFailure(ex);
            return Task.FromException(ex);
        }
    }

    private static async Task ObservePersistenceQueuesAsync(Task processing)
    {
        try
        {
            await processing;
            logger.Info(() => "Offline");
        }
        catch(Exception ex)
        {
            logger.Fatal(ex, "Share persistence queues terminated due to error");
            throw;
        }
    }

    private async Task RunPersistenceQueuesAsync(Channel<QueuedShare> queue,
        Channel<QueuedShare> emergencyQueue,
        BoundedQueueAccounting<QueuedShare> primaryAccounting,
        BoundedQueueAccounting<QueuedShare> emergencyAccounting)
    {
        var subscription = messageBus.Listen<Share>()
            .Where(x => x != null)
            .Subscribe(share => EnqueueShare(queue, emergencyQueue,
                    primaryAccounting, emergencyAccounting, share),
                ex =>
                {
                    queue.Writer.TryComplete(ex);
                    emergencyQueue.Writer.TryComplete(ex);
                },
                () =>
                {
                    queue.Writer.TryComplete();
                    emergencyQueue.Writer.TryComplete();
                });
        Volatile.Write(ref persistenceQueueWriter, queue.Writer);
        Volatile.Write(ref emergencyJournalQueueWriter,
            emergencyQueue.Writer);
        Interlocked.Exchange(ref shareSubscription, subscription)?.Dispose();

        try
        {
            var emergencyProcessing = ProcessEmergencyJournalQueueAsync(
                emergencyQueue.Reader, emergencyAccounting);

            try
            {
                await ProcessPersistenceQueueAsync(queue.Reader,
                    primaryAccounting, persistenceDrainCancellation.Token);
            }
            catch(OperationCanceledException) when(
                persistenceDrainCancellation.IsCancellationRequested)
            {
                // Establish the emergency writer's final durable outcome before snapshotting the
                // shared unresolved registry. This prevents a shutdown fallback from journalling
                // an item that the emergency writer has just committed independently.
                await emergencyProcessing;
                await JournalUnresolvedSharesOnShutdownAsync();
                return;
            }
            catch(TransactionCommitOutcomeUncertainException commitError)
            {
                QuiesceQueueIntake(subscription, queue, emergencyQueue,
                    commitError);
                await emergencyProcessing;
                await recoveryFailureHandler.StopClusterForUncertainCommitAsync(
                    SnapshotUnresolvedShares(), recoveryFilename, commitError);
                throw;
            }
            catch(Exception pipelineError)
            {
                QuiesceQueueIntake(subscription, queue, emergencyQueue,
                    pipelineError);
                await emergencyProcessing;
                await JournalUnresolvedSharesAfterPipelineFailureAsync(
                    pipelineError);
                throw;
            }

            await emergencyProcessing;
        }
        finally
        {
            Interlocked.CompareExchange(ref shareSubscription, null,
                subscription)?.Dispose();
            Volatile.Write(ref persistenceQueueWriter, null);
            Volatile.Write(ref emergencyJournalQueueWriter, null);
        }
    }

    private void QuiesceQueueIntake(IDisposable subscription,
        Channel<QueuedShare> queue, Channel<QueuedShare> emergencyQueue,
        Exception error)
    {
        failStopCoordinator.BeginFailStop(ProcessExitCodes.GeneralFailure);
        Interlocked.CompareExchange(ref shareSubscription, null,
            subscription)?.Dispose();
        queue.Writer.TryComplete(error);
        emergencyQueue.Writer.TryComplete();
    }

    private void EnqueueShare(Channel<QueuedShare> queue,
        Channel<QueuedShare> emergencyQueue,
        BoundedQueueAccounting<QueuedShare> primaryAccounting,
        BoundedQueueAccounting<QueuedShare> emergencyAccounting, Share share)
    {
        var queued = new QueuedShare(
            Interlocked.Increment(ref nextQueuedShareId), share);
        unresolvedShares[queued.Id] = queued;
        share.SetPersistenceAdmission(Task.CompletedTask);

        if(primaryAccounting.TryWrite(queue.Writer, queued))
            return;

        var saturation = new IOException(
            $"The bounded share-persistence queue reached its {PersistenceQueueCapacity}-share limit");
        var journalCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = journalCompletion.Task.ContinueWith(task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        queued.JournalCompletion = journalCompletion;
        share.SetPersistenceAdmission(journalCompletion.Task);

        if(emergencyAccounting.TryWrite(emergencyQueue.Writer, queued))
            return;

        var journalError = new IOException(
            $"The bounded emergency recovery-journal queue reached its " +
            $"{EmergencyJournalQueueCapacity}-share limit");
        journalCompletion.TrySetException(journalError);

        throw new SharePersistenceBacklogFailureException(
            () => FailStopUnresolvedSharesAsync(saturation, journalError),
            journalError);
    }

    private async Task ProcessEmergencyJournalQueueAsync(
        ChannelReader<QueuedShare> reader,
        BoundedQueueAccounting<QueuedShare> accounting)
    {
        const int batchSize = 250;
        var batch = new List<QueuedShare>(batchSize);

        while(await reader.WaitToReadAsync(CancellationToken.None))
        {
            batch.Clear();
            while(batch.Count < batchSize &&
                  accounting.TryRead(reader, out var queued))
                batch.Add(queued);

            if(batch.Count == 0)
                continue;

            try
            {
                // One force-flushed chained frame and terminal-anchor update accounts for the
                // whole drained set. This preserves the same atomic recovery boundary while
                // avoiding two fsync operations for every individual overflow share.
                await WriteRecoveryJournalAsync(batch.Select(x => x.Share)
                    .ToArray());

                foreach(var item in batch)
                {
                    unresolvedShares.TryRemove(item.Id, out _);
                    item.JournalCompletion?.TrySetResult();
                }

                NotifyAdminOnPolicyFallbackSafely();
                logger.Warn(
                    "Persisted {0} share(s) through the bounded emergency recovery-journal writer because the primary persistence queue was full",
                    batch.Count);
            }
            catch(Exception journalError)
            {
                var saturation = new IOException(
                    "The primary share-persistence queue was saturated");
                await FailStopUnresolvedSharesAsync(saturation, journalError);

                foreach(var item in batch)
                    item.JournalCompletion?.TrySetException(journalError);

                throw;
            }
        }
    }

    private async Task ProcessPersistenceQueueAsync(ChannelReader<QueuedShare> reader,
        BoundedQueueAccounting<QueuedShare> accounting, CancellationToken ct)
    {
        const int batchSize = 250;
        var batch = new List<QueuedShare>(batchSize);
        var batchStarted = DateTime.UtcNow;

        while(await reader.WaitToReadAsync(ct))
        {
            while(accounting.TryRead(reader, out var queued))
            {
                if(queued.Share.BlockOnly)
                {
                    // Preserve the prior immediate candidate lane: a candidate must not wait
                    // for an earlier partial ordinary-share batch to fill or flush.
                    await PersistQueuedSharesAsync(new[] { queued }, ct);
                    continue;
                }

                if(batch.Count == 0)
                    batchStarted = DateTime.UtcNow;

                batch.Add(queued);

                if(batch.Count >= batchSize)
                {
                    await PersistQueuedSharesAsync(batch.ToArray(), ct);
                    batch.Clear();
                    batchStarted = DateTime.UtcNow;
                }
            }

            if(batch.Count == 0)
                continue;

            var remaining = TimeSpan.FromSeconds(5) -
                (DateTime.UtcNow - batchStarted);

            if(remaining > TimeSpan.Zero)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(remaining);

                try
                {
                    if(await reader.WaitToReadAsync(wait.Token))
                        continue;
                }
                catch(OperationCanceledException) when(
                    !ct.IsCancellationRequested && wait.IsCancellationRequested)
                {
                    // The batch dwell deadline expired. The only channel waiter was cancelled
                    // and observed before this partial batch is persisted.
                }
            }

            await PersistQueuedSharesAsync(batch.ToArray(), ct);
            batch.Clear();
            batchStarted = DateTime.UtcNow;
        }

        if(batch.Count > 0)
            await PersistQueuedSharesAsync(batch, ct);
    }

    private async Task PersistQueuedSharesAsync(IReadOnlyCollection<QueuedShare> queued,
        CancellationToken ct)
    {
        try
        {
            await PersistSharesAsync(queued.Select(x => x.Share).ToArray(), ct);
        }
        catch(TransactionCommittedCleanupException)
        {
            // Commit completed before provider cleanup failed. Remove this exact batch before
            // the outer durability boundary snapshots or journals anything; replaying it would
            // duplicate share and potentially block accounting.
            foreach(var item in queued)
                unresolvedShares.TryRemove(item.Id, out _);

            throw;
        }

        foreach(var item in queued)
            unresolvedShares.TryRemove(item.Id, out _);
    }

    private async Task JournalUnresolvedSharesOnShutdownAsync()
    {
        var unresolved = SnapshotUnresolvedShares();

        if(unresolved.Length == 0)
            return;

        var databaseError = new TimeoutException(
            "The hosted-service shutdown deadline expired before PostgreSQL drained the admitted share backlog");

        try
        {
            await WriteRecoveryJournalAsync(unresolved);
            NotifyAdminOnPolicyFallbackSafely();

            foreach(var item in unresolvedShares.ToArray())
            {
                if(unresolvedShares.TryRemove(item.Key, out var removed))
                    removed.JournalCompletion?.TrySetResult();
            }
        }
        catch(Exception journalError)
        {
            await FailStopUnresolvedSharesAsync(databaseError, journalError);
            throw;
        }
    }

    private async Task JournalUnresolvedSharesAfterPipelineFailureAsync(
        Exception pipelineError)
    {
        var unresolved = SnapshotUnresolvedShares();
        if(unresolved.Length == 0)
        {
            await recoveryFailureHandler.StopClusterAfterJournalAsync(
                unresolved, recoveryFilename, pipelineError);
            return;
        }

        try
        {
            await WriteRecoveryJournalAsync(unresolved);
            NotifyAdminOnPolicyFallbackSafely();

            foreach(var item in unresolvedShares.ToArray())
            {
                if(unresolvedShares.TryRemove(item.Key, out var removed))
                    removed.JournalCompletion?.TrySetResult();
            }

            await recoveryFailureHandler.StopClusterAfterJournalAsync(
                unresolved, recoveryFilename, pipelineError);
        }
        catch(Exception journalError)
        {
            pipelineError.Data["RecoveryJournalException"] = journalError;
            await FailStopUnresolvedSharesAsync(pipelineError, journalError);
            throw new IOException(
                "An unexpected share-persistence failure was followed by recovery-journal failure",
                new AggregateException(pipelineError, journalError));
        }
    }

    private Share[] SnapshotUnresolvedShares(Share additional = null)
    {
        var shares = unresolvedShares.Values
            .OrderBy(x => x.Id)
            .Select(x => x.Share)
            .ToList();

        if(additional != null && !shares.Contains(additional))
            shares.Add(additional);

        return shares.ToArray();
    }

    private Task FailStopUnresolvedSharesAsync(Exception databaseError,
        Exception journalError)
    {
        persistenceDrainCancellation.Cancel();
        var captured = failStopCoordinator.BeginFailStopAndCapture(
            ProcessExitCodes.UnreconciledShareDurabilityLoss,
            () => unresolvedShares.Values.ToArray());
        var unresolved = captured
            .OrderBy(x => x.Id)
            .Select(x => x.Share)
            .ToArray();

        // The exclusive admission boundary is already closed and captured above. Move durable
        // fatal evidence and the bounded operator notification off the synchronous Stratum/Rx
        // publication thread while retaining an awaitable task for hosted queue processing.
        var handling = Task.Factory.StartNew(() =>
            {
                // A dedicated thread guarantees that fatal evidence can make progress even when
                // the shared pool is starved. Blocking the dedicated worker across asynchronous
                // notification I/O cannot consume a Stratum, Rx or general thread-pool worker.
                recoveryFailureHandler.StopClusterAsync(unresolved,
                        recoveryFilename, databaseError, journalError)
                    .GetAwaiter().GetResult();

                foreach(var queued in unresolvedShares.Values)
                    queued.JournalCompletion?.TrySetException(journalError);
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        lock(deferredFailStopGate)
            deferredFailStopHandling.Add(handling);
        return handling;
    }

    private void AcquireRecorderRecoveryOwnership()
    {
        // Production preflight already owns the path for the process lifetime. Direct service
        // fixtures do not run preflight, so acquire exactly one recorder-scoped hold only when the
        // shared process hold is absent and balance only that hold during StopAsync.
        if(recoveryPathOwnership is not TestShareRecoveryPathOwnership &&
           recoveryPathOwnership.IsHeld)
            return;

        recoveryPathOwnership.Acquire();
        Volatile.Write(ref recorderRecoveryOwnershipHeld, 1);
    }

    private void ReleaseRecorderRecoveryOwnership()
    {
        if(Interlocked.Exchange(ref recorderRecoveryOwnershipHeld, 0) != 0)
            recoveryPathOwnership.Release();
    }

    private sealed class QueuedShare
    {
        public QueuedShare(long id, Share share)
        {
            Id = id;
            Share = share;
        }

        public long Id { get; }
        public Share Share { get; }
        public TaskCompletionSource JournalCompletion { get; set; }
    }

    private sealed class SharePersistenceBacklogFailureException : IOException,
        IMiningAdmissionFailure
    {
        public SharePersistenceBacklogFailureException(
            Func<Task> failStop, Exception journalError) :
            base("The bounded share-persistence queue and recovery journal were both unavailable",
                journalError)
        {
            this.failStop = failStop ??
                throw new ArgumentNullException(nameof(failStop));
        }

        private readonly Func<Task> failStop;

        public Task HandleAfterAdmissionReleasedAsync() => failStop();
    }
}
