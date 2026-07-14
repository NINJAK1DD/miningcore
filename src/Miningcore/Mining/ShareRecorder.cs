using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using AutoMapper;
using Microsoft.Extensions.Hosting;
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
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Mining;

/// <summary>
/// Asynchronously persist shares produced by all pools for processing by coin-specific payment processor(s)
/// </summary>
public class ShareRecorder : BackgroundService, IBlockCandidateRecorder
{
    public ShareRecorder(IConnectionFactory cf,
        IMapper mapper,
        JsonSerializerSettings jsonSerializerSettings,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        ClusterConfig clusterConfig,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(jsonSerializerSettings);
        Contract.RequiresNonNull(messageBus);

        this.cf = cf;
        this.mapper = mapper;
        this.jsonSerializerSettings = jsonSerializerSettings;
        this.messageBus = messageBus;
        this.clusterConfig = clusterConfig;

        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;

        pools = clusterConfig.Pools.ToDictionary(x => x.Id, x => x);

        BuildFaultHandlingPolicy();
        ConfigureRecovery();
        var recoveryPath = Path.GetFullPath(recoveryFilename);
        recoveryWriteGate = RecoveryWriteGates.GetOrAdd(recoveryPath,
            _ => new SemaphoreSlim(1, 1));
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IShareRepository shareRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly JsonSerializerSettings jsonSerializerSettings;
    private readonly IMessageBus messageBus;
    private readonly ClusterConfig clusterConfig;
    private readonly Dictionary<string, PoolConfig> pools;
    private readonly IMapper mapper;

    private IAsyncPolicy faultPolicy;
    private bool hasLoggedPolicyFallbackFailure;
    private string recoveryFilename;
    private const int RetryCount = 3;
    private const string PolicyContextKeyShares = "share";
    private bool notifiedAdminOnPolicyFallback = false;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RecoveryWriteGates =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private readonly SemaphoreSlim recoveryWriteGate;
    private readonly CancellationTokenSource blockCandidateShutdown = new();
    private int blockCandidateShutdownStarted;
    internal TimeSpan ShutdownDatabaseAttemptTimeout { get; set; } =
        TimeSpan.FromSeconds(5);
    internal SemaphoreSlim RecoveryWriteGate => recoveryWriteGate;
    private static readonly HashSet<string> UncertainBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auxpow-claim",
        "parent-uncertain",
        "merged-parent-uncertain",
    };

    private async Task PersistSharesAsync(IList<Share> shares)
    {
        var context = new Dictionary<string, object> { { PolicyContextKeyShares, shares } };

        await faultPolicy.ExecuteAsync(ctx => PersistSharesCoreAsync((IList<Share>) ctx[PolicyContextKeyShares]), context);
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

        try
        {
            await WriteRecoveryJournalAsync(shares);
            NotifyAdminOnPolicyFallback();
        }
        catch(Exception ex)
        {
            if(!hasLoggedPolicyFallbackFailure)
            {
                logger.Fatal(ex, "Fatal error during candidate recovery fallback. Block candidate will be lost!");
                hasLoggedPolicyFallbackFailure = true;
            }

            throw new IOException(
                "Unable to durably persist a block candidate to PostgreSQL or the recovery journal",
                ex);
        }
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

    internal async Task PersistSharesCoreAsync(IList<Share> shares)
    {
        var insertedBlocks = await cf.RunTx((con, tx) =>
            PersistSharesBatchAsync(con, tx, shares));

        NotifyPersistedBlocks(insertedBlocks);
    }

    private async Task<List<(string PoolId, Block Block)>> PersistSharesBatchAsync(
        IDbConnection con, IDbTransaction tx, IList<Share> shares)
    {
        var result = new List<(string PoolId, Block Block)>();

        // Block-only candidates are sent through the share message pipeline so they can reuse
        // normal block persistence and notifications without creating a duplicate standalone
        // share row or distorting hashrate, effort, and share statistics.
        var mapped = GetSharesForPersistence(shares)
            .Select(mapper.Map<Persistence.Model.Share>)
            .ToArray();

        if(mapped.Length > 0)
            await shareRepo.BatchInsertAsync(con, tx, mapped, CancellationToken.None);

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
                if(pools.TryGetValue(poolId, out var poolConfig))
                    messageBus.NotifyBlockFound(poolId, block, poolConfig.Template);
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
        logger.Warn(() => $"Fallback due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
        return Task.CompletedTask;
    }

    private async Task OnExecutePolicyFallbackAsync(Context context, CancellationToken ct)
    {
        var shares = (IList<Share>) context[PolicyContextKeyShares];

        try
        {
            await WriteRecoveryJournalAsync(shares);
            NotifyAdminOnPolicyFallback();
        }

        catch(Exception ex)
        {
            if(!hasLoggedPolicyFallbackFailure)
            {
                logger.Fatal(ex, "Fatal error during policy fallback execution. Share(s) will be lost!");
                hasLoggedPolicyFallbackFailure = true;
            }

            if(shares.Any(x => x.BlockOnly))
                throw new IOException(
                    "Unable to durably persist a block candidate to PostgreSQL or the recovery journal",
                    ex);
        }
    }

    internal async Task WriteRecoveryJournalAsync(IList<Share> shares)
    {
        await recoveryWriteGate.WaitAsync(CancellationToken.None);

        try
        {
            await using var stream = new FileStream(recoveryFilename, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            });
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false),
                1024, true);

            if(stream.Length == 0)
                WriteRecoveryFileheader(writer);

            foreach(var share in shares)
            {
                var json = JsonConvert.SerializeObject(share, jsonSerializerSettings);
                await writer.WriteLineAsync(json);
            }

            await writer.FlushAsync();
            stream.Flush(true);
        }
        finally
        {
            recoveryWriteGate.Release();
        }
    }

    private static void WriteRecoveryFileheader(TextWriter writer)
    {
        writer.WriteLine("# The existence of this file means shares could not be committed to the database.");
        writer.WriteLine("# You should stop the pool cluster and run the following command:");
        writer.WriteLine("# miningcore -c <path-to-config> -rs <path-to-this-file>\n");
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
            using(var reader = new StreamReader(stream, new UTF8Encoding(false)))
            {
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

        while(!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
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

    private void ConfigureRecovery()
    {
        recoveryFilename = !string.IsNullOrEmpty(clusterConfig.ShareRecoveryFile)
            ? clusterConfig.ShareRecoveryFile
            : "recovered-shares.txt";
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
            .FallbackAsync(OnExecutePolicyFallbackAsync, (ex, context) => Task.CompletedTask);

        faultPolicy = Policy.WrapAsync(
            fallbackOnBrokenCircuit,
            Policy.WrapAsync(fallback, breaker, retry));
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        BeginShutdown();
        await base.StopAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        logger.Info(() => "Online");

        return messageBus.Listen<Share>()
            .ObserveOn(TaskPoolScheduler.Default)
            .Where(x => x != null)
            .Publish(shares => shares
                // Minimize the in-memory durability window for accepted or uncertain block
                // candidates. Ordinary shares retain the existing batching behavior.
                .Where(x => x.BlockOnly)
                .Select(x => (IList<Share>) new[] { x })
                .Merge(shares
                    .Where(x => !x.BlockOnly)
                    .Buffer(TimeSpan.FromSeconds(5), 250)
                    .Where(batch => batch.Any())
                    .Select(batch => (IList<Share>) batch)))
            .Select(shares => Observable.FromAsync(() =>
                Guard(() =>
                        PersistSharesAsync(shares),
                    ex => logger.Error(ex))))
            .Concat()
            .ToTask(ct)
            .ContinueWith(task =>
            {
                if(task.IsFaulted)
                    logger.Fatal(() => $"Terminated due to error {task.Exception?.InnerException ?? task.Exception}");
                else
                    logger.Info(() => "Offline");
            }, ct);
    }
}
