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
public class ShareRecorder : StartupGatedBackgroundService, IBlockCandidateRecorder
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
            candidateFailureHandler)
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
        ICandidatePersistenceFailureHandler candidateFailureHandler = null)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(jsonSerializerSettings);
        Contract.RequiresNonNull(messageBus);
        ArgumentNullException.ThrowIfNull(recoveryFailureHandler);

        this.cf = cf;
        this.mapper = mapper;
        this.jsonSerializerSettings = jsonSerializerSettings;
        this.messageBus = messageBus;
        this.candidateFailureHandler = candidateFailureHandler ??
            NullCandidatePersistenceFailureHandler.Instance;
        this.recoveryFailureHandler = recoveryFailureHandler;
        this.clusterConfig = clusterConfig;

        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;

        pools = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

        BuildFaultHandlingPolicy();
        recoveryFilename = ShareRecoveryFatalState.ResolveRecoveryFilename(clusterConfig);
        recoveryTerminalState = new ShareRecoveryTerminalState(recoveryFilename,
            ShareRecoveryFatalState.ResolveStateDirectory(clusterConfig));
        RecoveryTerminalStateWrite = recoveryTerminalState.Write;
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
    internal string RecoveryTerminalStateFilename => recoveryTerminalState.Filename;
    internal Action<long, string> RecoveryTerminalStateWrite { get; set; }
    private readonly CancellationTokenSource blockCandidateShutdown = new();
    private int blockCandidateShutdownStarted;
    internal TimeSpan ShutdownDatabaseAttemptTimeout { get; set; } =
        TimeSpan.FromSeconds(5);
    internal int PersistenceQueueCapacity { get; set; } = 65_536;
    internal int EmergencyJournalQueueCapacity { get; set; } = 1_024;
    internal int PersistenceQueueHighWatermark =>
        Volatile.Read(ref persistenceQueueHighWatermark);
    private int persistenceQueueHighWatermark;
    private IDisposable shareSubscription;
    private ChannelWriter<QueuedShare> persistenceQueueWriter;
    private ChannelWriter<QueuedShare> emergencyJournalQueueWriter;
    private readonly CancellationTokenSource persistenceDrainCancellation = new();
    private readonly ConcurrentDictionary<long, QueuedShare> unresolvedShares = new();
    private long nextQueuedShareId;
    internal SemaphoreSlim RecoveryWriteGate => recoveryWriteState.Gate;
    internal long RecoveryValidationBytesRead =>
        Interlocked.Read(ref recoveryValidationBytesRead);
    private long recoveryValidationBytesRead;
    internal Action<string> RecoveryDirectorySync { get; set; } =
        ShareRecoveryFatalState.SyncDirectoryWhereSupported;
    internal Func<Stream, Task> RecoveryJournalFlush { get; set; } =
        FlushRecoveryJournalAsync;
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
            PersistSharesBatchAsync(con, tx, shares, ct));

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
            var inserted = await blockRepo.InsertAsync(con, tx, blockEntity);

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
            FileStream stream;

            try
            {
                stream = new FileStream(recoveryFilename, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                });
            }
            catch(FileNotFoundException)
            {
                if(recoveryWriteState.IsTrusted)
                    throw new InvalidDataException(
                        $"Recovery journal {recoveryFilename} disappeared after its append tail was validated. " +
                        "Preserve the surrounding storage for reconciliation; refusing to create a replacement.");

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
            await using(var stream = new FileStream(temporary,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                }))
            {
                var payload = BuildRecoveryJournalPayload(shares, true,
                    1, EmptyFrameDigest);
                await AppendRecoveryJournalAsync(stream, payload.Bytes,
                    RecoveryJournalFlush);
            }

            // A force-flushed file is not a durable first creation until its directory entry is
            // atomically published and the containing directory is synchronised on Linux.
            File.Move(temporary, recoveryFilename, false);
            RecoveryDirectorySync(directory);

            await using var active = new FileStream(recoveryFilename,
                FileMode.Open, FileAccess.Read, FileShare.Read);
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
                File.Delete(temporary);
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
        using var lines = new BoundedRecoveryLineReader(reader,
            MaxRecoveryRecordLineLength, filename);

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

    private static void ValidateRecoveryHeader(BoundedRecoveryLineReader lines,
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
        BoundedRecoveryLineReader lines, string filename, string firstLine,
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

    private static void ValidateRecoveryV1Frame(BoundedRecoveryLineReader lines,
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
        BoundedRecoveryLineReader lines, string filename, string firstLine,
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
            throw new InvalidOperationException("Unreachable recovery-journal append path");
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

    private sealed class BoundedRecoveryLineReader : IDisposable
    {
        public BoundedRecoveryLineReader(TextReader reader, int maximumLength,
            string filename)
        {
            this.reader = reader;
            this.maximumLength = maximumLength;
            this.filename = filename;
        }

        private readonly TextReader reader;
        private readonly int maximumLength;
        private readonly string filename;
        private readonly char[] buffer = new char[4096];
        private int position;
        private int available;
        private long lineNumber;

        public string ReadLine()
        {
            StringBuilder builder = null;
            var length = 0;

            while(true)
            {
                if(position >= available)
                {
                    available = reader.Read(buffer, 0, buffer.Length);
                    position = 0;

                    if(available == 0)
                    {
                        if(builder == null && length == 0)
                            return null;

                        lineNumber++;
                        return Finish(builder, length);
                    }
                }

                var newline = Array.IndexOf(buffer, '\n', position,
                    available - position);
                var end = newline >= 0 ? newline : available;
                var segmentLength = end - position;
                length = checked(length + segmentLength);

                if(length > maximumLength)
                    throw new InvalidDataException(
                        $"Recovery journal {filename} contains a record line longer than " +
                        $"{maximumLength} characters near line {lineNumber + 1}. Preserve it for reconciliation.");

                builder ??= new StringBuilder(Math.Min(maximumLength,
                    Math.Max(128, length)));
                builder.Append(buffer, position, segmentLength);
                position = newline >= 0 ? newline + 1 : available;

                if(newline < 0)
                    continue;

                lineNumber++;
                return Finish(builder, length);
            }
        }

        private static string Finish(StringBuilder builder, int length)
        {
            if(builder == null)
                return string.Empty;

            if(length > 0 && builder[length - 1] == '\r')
                builder.Length--;

            return builder.ToString();
        }

        public void Dispose()
        {
            // The caller owns the underlying StreamReader and stream.
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
    }

    public async Task<string> RecoverSharesAsync(string filename)
    {
        logger.Info(() => $"Recovering shares using {filename} ...");

        try
        {
            List<(string PoolId, Block Block)> insertedBlocks;
            int validatedCount;
            string fileHash;

            // Hold one read-only handle across both passes. FileShare.Read permits diagnostics
            // and backups, but prevents the recovery source from being changed between validation
            // and import.
            await using(var stream = new FileStream(filename, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            }))
            {
                // Enforce every count/hash frame before opening the database transaction. The
                // same read-only handle remains locked for both semantic passes, preventing the
                // source from changing after its byte-level integrity was established.
                EnsureRecoveryJournalAppendBoundary(stream, filename);
                if(string.Equals(Path.GetFullPath(filename), recoveryFilename,
                       OperatingSystem.IsWindows()
                           ? StringComparison.OrdinalIgnoreCase
                           : StringComparison.Ordinal))
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    var tail = ValidateRecoveryJournalDetailed(stream, filename);
                    recoveryTerminalState.EnsureConsistent(tail.Sequence,
                        tail.FrameDigest);
                }
                stream.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));

                // Pass one validates every record before a database transaction is opened.
                var validationHash = new RecoveryContentHasher(jsonSerializerSettings);
                validatedCount = await ProcessRecoveryRecordsAsync(reader, shares =>
                {
                    validationHash.Append(shares);
                    return Task.CompletedTask;
                });
                fileHash = validationHash.GetHash();

                reader.DiscardBufferedData();
                stream.Seek(0, SeekOrigin.Begin);

                // Pass two imports every batch through one transaction. Any parse or database
                // failure rolls back the complete file, making a non-zero exit safe to retry.
                insertedBlocks = await cf.RunTx(async (con, tx) =>
                {
                    var registered = await shareRepo.TryRegisterRecoveryImportAsync(con,
                        tx, fileHash, Path.GetFileName(filename), validatedCount,
                        CancellationToken.None);

                    if(!registered)
                        throw new InvalidOperationException(
                            $"Recovery content from {filename} was already imported [{fileHash}]");

                    var result = new List<(string PoolId, Block Block)>();
                    int importedCount;
                    string importedHash;

                    var importHash = new RecoveryContentHasher(jsonSerializerSettings);
                    importedCount = await ProcessRecoveryRecordsAsync(reader,
                        async shares =>
                        {
                            importHash.Append(shares);
                            result.AddRange(await PersistSharesBatchAsync(con, tx, shares));
                        });
                    importedHash = importHash.GetHash();

                    if(importedCount != validatedCount ||
                       !string.Equals(importedHash, fileHash,
                           StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Recovery source changed between validation and import");
                    }

                    return result;
                });
            }

            var archiveFilename = ArchiveImportedRecoveryFile(filename);
            if(archiveFilename != null &&
               string.Equals(Path.GetFullPath(filename), recoveryFilename,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal))
                recoveryTerminalState.RemoveAfterArchive();
            NotifyPersistedBlocks(insertedBlocks);
            logger.Info(() => $"Successfully imported {validatedCount} shares" +
                (archiveFilename != null ? $" and archived the source as {archiveFilename}" :
                    "; the database import manifest will prevent replay"));
            return archiveFilename;
        }

        catch(FileNotFoundException)
        {
            logger.Error(() => $"Recovery file {filename} was not found");
            throw;
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

    private static string ArchiveImportedRecoveryFile(string filename)
    {
        var archiveFilename = $"{filename}.imported-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";

        try
        {
            File.Move(filename, archiveFilename);
            return archiveFilename;
        }

        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn(ex, () => $"Unable to archive successfully imported recovery file {filename}. Do not retry it; the database import manifest will reject the same content");
            return null;
        }
    }

    private async Task<int> ProcessRecoveryRecordsAsync(StreamReader reader,
        Func<IList<Share>, Task> processBatch)
    {
        const int bufferSize = 100;
        var shares = new List<Share>(bufferSize);
        var recordCount = 0;
        var lineNumber = 0;
        using var lines = new BoundedRecoveryLineReader(reader,
            MaxRecoveryRecordLineLength, "recovery import source");

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
        BeginShutdown();
        Interlocked.Exchange(ref shareSubscription, null)?.Dispose();
        Volatile.Read(ref persistenceQueueWriter)?.TryComplete();
        Volatile.Read(ref emergencyJournalQueueWriter)?.TryComplete();

        try
        {
            if(ExecuteTask != null)
                await ExecuteTask.WaitAsync(ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // The hosted-service deadline closes database work, not accounting. The queue worker
            // catches this cancellation and force-flushes its complete unresolved registry to the
            // recovery journal before StopAsync is allowed to finish.
            persistenceDrainCancellation.Cancel();

            if(ExecuteTask != null)
                await ExecuteTask;
        }

        await base.StopAsync(CancellationToken.None);
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
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
            var processing = ObservePersistenceQueuesAsync(
                RunPersistenceQueuesAsync(queue, emergencyQueue));

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
        Channel<QueuedShare> emergencyQueue)
    {
        var subscription = messageBus.Listen<Share>()
            .Where(x => x != null)
            .Subscribe(share => EnqueueShare(queue, emergencyQueue, share),
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
                emergencyQueue.Reader);

            try
            {
                await ProcessPersistenceQueueAsync(queue.Reader,
                    persistenceDrainCancellation.Token);
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

    private void EnqueueShare(Channel<QueuedShare> queue,
        Channel<QueuedShare> emergencyQueue, Share share)
    {
        var queued = new QueuedShare(
            Interlocked.Increment(ref nextQueuedShareId), share);
        unresolvedShares[queued.Id] = queued;
        share.SetPersistenceAdmission(Task.CompletedTask);

        if(queue.Writer.TryWrite(queued))
        {
            UpdatePersistenceQueueHighWatermark(queue.Reader.Count);
            return;
        }

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

        if(emergencyQueue.Writer.TryWrite(queued))
            return;

        unresolvedShares.TryRemove(queued.Id, out _);
        var journalError = new IOException(
            $"The bounded emergency recovery-journal queue reached its " +
            $"{EmergencyJournalQueueCapacity}-share limit");
        journalCompletion.TrySetException(journalError);

        throw new SharePersistenceBacklogFailureException(
            recoveryFailureHandler, SnapshotUnresolvedShares(share),
            recoveryFilename, saturation, journalError);
    }

    private async Task ProcessEmergencyJournalQueueAsync(
        ChannelReader<QueuedShare> reader)
    {
        await foreach(var queued in reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await WriteRecoveryJournalAsync(new[] { queued.Share });
                unresolvedShares.TryRemove(queued.Id, out _);
                NotifyAdminOnPolicyFallbackSafely();
                queued.JournalCompletion?.TrySetResult();
                logger.Warn(
                    "Persisted a share through the bounded emergency recovery-journal writer because the primary persistence queue was full");
            }
            catch(Exception journalError)
            {
                var saturation = new IOException(
                    "The primary share-persistence queue was saturated");
                await FailStopUnresolvedSharesAsync(saturation, journalError);
                queued.JournalCompletion?.TrySetException(journalError);
                throw;
            }
        }
    }

    private void UpdatePersistenceQueueHighWatermark(int count)
    {
        while(true)
        {
            var previous = Volatile.Read(ref persistenceQueueHighWatermark);

            if(previous >= count)
                return;

            if(Interlocked.CompareExchange(ref persistenceQueueHighWatermark,
                   count, previous) == previous)
                return;
        }
    }

    private async Task ProcessPersistenceQueueAsync(ChannelReader<QueuedShare> reader,
        CancellationToken ct)
    {
        const int batchSize = 250;
        var batch = new List<QueuedShare>(batchSize);
        var batchStarted = DateTime.UtcNow;

        while(await reader.WaitToReadAsync(ct))
        {
            while(reader.TryRead(out var queued))
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
                var available = reader.WaitToReadAsync(ct).AsTask();
                var timer = Task.Delay(remaining, ct);

                if(await Task.WhenAny(available, timer) == available &&
                   await available)
                    continue;
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
        await PersistSharesAsync(queued.Select(x => x.Share).ToArray(), ct);

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
                unresolvedShares.TryRemove(item.Key, out _);
        }
        catch(Exception journalError)
        {
            await FailStopUnresolvedSharesAsync(databaseError, journalError);
            throw;
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

    private async Task FailStopUnresolvedSharesAsync(Exception databaseError,
        Exception journalError)
    {
        persistenceDrainCancellation.Cancel();
        var unresolved = SnapshotUnresolvedShares();

        await recoveryFailureHandler.StopClusterAsync(unresolved,
            recoveryFilename, databaseError, journalError);

        foreach(var queued in unresolvedShares.Values)
            queued.JournalCompletion?.TrySetException(journalError);
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
            IShareRecoveryFailureHandler failureHandler, Share[] shares,
            string recoveryFilename, Exception backlogError,
            Exception journalError) :
            base("The bounded share-persistence queue and recovery journal were both unavailable",
                journalError)
        {
            this.failureHandler = failureHandler;
            this.shares = shares;
            this.recoveryFilename = recoveryFilename;
            this.backlogError = backlogError;
            this.journalError = journalError;
        }

        private readonly IShareRecoveryFailureHandler failureHandler;
        private readonly Share[] shares;
        private readonly string recoveryFilename;
        private readonly Exception backlogError;
        private readonly Exception journalError;

        public Task HandleAfterAdmissionReleasedAsync() =>
            failureHandler.StopClusterAsync(shares, recoveryFilename,
                backlogError, journalError);
    }
}
