using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Miningcore.Configuration;
using Newtonsoft.Json;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Mining;

public interface IShareRecoveryFatalState
{
    string RecoveryFilename { get; }
    string FatalStateFilename { get; }
    void EnsureStartupAllowed();
    void MarkFatal(int shareCount, IReadOnlyCollection<string> pools,
        Exception databaseError, Exception journalError);
    void MarkFatalShares(IReadOnlyCollection<Share> shares,
        Exception databaseError, Exception journalError,
        string failureCategory = "database-and-journal-unavailable");
}

public sealed class ShareRecoveryFatalState : IShareRecoveryFatalState
{
    public ShareRecoveryFatalState(ClusterConfig clusterConfig,
        IProcessStatus processStatus) :
        this(clusterConfig, processStatus, ResolveStateDirectory(clusterConfig))
    {
    }

    internal ShareRecoveryFatalState(ClusterConfig clusterConfig,
        IProcessStatus processStatus, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(clusterConfig);
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        this.processStatus = processStatus;
        RecoveryFilename = ResolveRecoveryFilename(clusterConfig);
        StateDirectory = Path.GetFullPath(stateDirectory);
        var pathHash = ComputeRecoveryPathHash(RecoveryFilename);
        FatalStateFilename = Path.Combine(StateDirectory,
            "share-recovery-fatal", pathHash + ".fatal");
        terminalState = new ShareRecoveryTerminalState(RecoveryFilename,
            StateDirectory);
        importState = new ShareRecoveryImportState(RecoveryFilename,
            StateDirectory);
    }

    private readonly IProcessStatus processStatus;
    private readonly object fatalStateGate = new();
    private readonly ShareRecoveryTerminalState terminalState;
    private readonly ShareRecoveryImportState importState;
    internal Action<string> DirectorySync { get; set; } =
        SyncDirectoryWhereSupported;
    internal Action<int> ExactShareWriteCheckpoint { get; set; } = _ => { };

    public string RecoveryFilename { get; }
    public string StateDirectory { get; }
    public string FatalStateFilename { get; }

    public void EnsureStartupAllowed()
    {
        try
        {
            var markerEntryWasPresent = EnsureFatalStateDirectoryAccessible();
            terminalState.DirectorySync = DirectorySync;
            importState.DirectorySync = DirectorySync;
            terminalState.EnsureDirectoryDurable();
            importState.EnsureDirectoryDurable();

            try
            {
                using var marker = new FileStream(FatalStateFilename, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                _ = marker.ReadByte();

                throw new PoolStartupException(
                    $"Share-accounting durability remains unreconciled. Fatal state: " +
                    $"{FatalStateFilename}. Recovery journal: {RecoveryFilename}. Preserve both " +
                    "files, reconcile every recorded incident against PostgreSQL and the journal, then remove only the exact " +
                    "fatal-state file as the explicit operator acknowledgement before restarting " +
                    "Miningcore.");
            }
            catch(FileNotFoundException) when(!markerEntryWasPresent)
            {
                // The independently owned state directory is accessible and this exact marker
                // does not exist. Other metadata and I/O failures are deliberately fail-closed.
            }

            try
            {
                importState.EnsureNoOutstandingImport();
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                                      UnauthorizedAccessException)
            {
                throw new PoolStartupException(
                    $"Recovery-import retirement validation failed for {RecoveryFilename}: " +
                    $"{ex.Message}");
            }

            try
            {
                using var stream = new FileStream(RecoveryFilename, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                ShareRecorder.EnsureRecoveryJournalAppendBoundary(stream,
                    RecoveryFilename);
                stream.Seek(0, SeekOrigin.Begin);
                var tail = ShareRecorder.ValidateRecoveryJournalDetailed(stream,
                    RecoveryFilename);
                terminalState.EnsureConsistent(tail.Sequence, tail.FrameDigest,
                    tail.IsChainedFormat);
            }
            catch(FileNotFoundException)
            {
                // A journal is created only when PostgreSQL first needs the fallback.
                terminalState.EnsureJournalMayBeMissing();
            }
            catch(DirectoryNotFoundException)
            {
                // A not-yet-created configured journal parent is equivalent to no journal.
                terminalState.EnsureJournalMayBeMissing();
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or
                UnauthorizedAccessException)
            {
                throw new PoolStartupException(
                    $"Recovery journal startup validation failed for {RecoveryFilename}: " +
                    $"{ex.Message} Preserve it for reconciliation before restarting Miningcore.");
            }
        }
        catch(PoolStartupException)
        {
            processStatus.MarkFailed(ProcessExitCodes.UnreconciledShareDurabilityLoss);
            throw;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            processStatus.MarkFailed(ProcessExitCodes.UnreconciledShareDurabilityLoss);
            throw new PoolStartupException(
                $"Unable to determine the share-recovery fatal state at " +
                $"{FatalStateFilename}: {ex.Message} Startup is blocked because fatal-state " +
                "uncertainty must be reconciled before mining can resume.");
        }
    }

    public void MarkFatal(int shareCount, IReadOnlyCollection<string> pools,
        Exception databaseError, Exception journalError)
    {
        lock(fatalStateGate)
            MarkFatalCore(shareCount, pools, null, databaseError, journalError,
                "database-and-journal-unavailable");
    }

    public void MarkFatalShares(IReadOnlyCollection<Share> shares,
        Exception databaseError, Exception journalError,
        string failureCategory = "database-and-journal-unavailable")
    {
        ArgumentNullException.ThrowIfNull(shares);

        lock(fatalStateGate)
            MarkFatalCore(shares.Count, null, shares, databaseError,
                journalError, failureCategory);
    }

    private void MarkFatalCore(int shareCount, IReadOnlyCollection<string> pools,
        IReadOnlyCollection<Share> shares, Exception databaseError,
        Exception journalError, string failureCategory)
    {
        _ = EnsureFatalStateDirectoryAccessible();
        var recoveryPathHash = ComputeRecoveryPathHash(RecoveryFilename);
        var incidentId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var directory = Path.GetDirectoryName(FatalStateFilename)!;
        var stem = Path.GetFileNameWithoutExtension(FatalStateFilename);
        var detailFilename = shares != null
            ? Path.Combine(directory, $"{stem}.{incidentId}.shares")
            : null;
        string detailHash = null;

        var created = DateTimeOffset.UtcNow;
        var initialState = BuildIncidentLatch(incidentId, created,
            recoveryPathHash, failureCategory, shareCount, pools, databaseError,
            journalError, detailFilename, detailHash,
            shares == null ? "not-required" : "hash-pending",
            shares != null);
        var incidentFilename = Path.Combine(directory,
            $"{stem}.{incidentId}.incident");

        // Publish the small authoritative startup barrier before enumerating or serializing any
        // exact shares. A serialization, memory-pressure or sidecar write failure must never
        // leave a later restart unblocked.
        WriteSmallStateFile(FatalStateFilename, initialState, true);
        WriteSmallStateFile(incidentFilename, initialState, false);

        if(shares != null)
        {
            var detail = WriteExactShareSidecar(detailFilename, shares);
            detailHash = detail.Hash;
            pools = detail.Pools;
        }

        var completedState = BuildIncidentLatch(incidentId, created,
            recoveryPathHash, failureCategory, shareCount, pools, databaseError,
            journalError, detailFilename, detailHash,
            shares == null ? "not-required" : "complete");
        // Each incident record is advanced once from hash-pending to complete, then remains
        // immutable evidence. The fixed-name latch remains a small current startup barrier.
        WriteSmallStateFile(incidentFilename, completedState, true);
        WriteSmallStateFile(FatalStateFilename, completedState, true);
    }

    private string BuildIncidentLatch(string incidentId, DateTimeOffset created,
        string recoveryPathHash, string failureCategory, int shareCount,
        IReadOnlyCollection<string> pools, Exception databaseError,
        Exception journalError, string detailFilename, string detailHash,
        string detailState, bool poolsPending = false)
    {
        return string.Join('\n',
            "Miningcore share-accounting durability failure",
            "formatVersion=2",
            $"incidentId={incidentId}",
            $"createdUtc={created:O}",
            $"failureCategory={SanitizeStateValue(failureCategory, 256)}",
            $"recoveryFile={RecoveryFilename}",
            $"recoveryPathSha256={recoveryPathHash}",
            $"shareCount={shareCount}",
            $"pools={(poolsPending ? "(pending)" : SanitizeStateValue(
                string.Join(',', pools ?? Array.Empty<string>()), 4096))}",
            $"detailFile={detailFilename ?? "(none)"}",
            $"detailSha256={detailHash ?? "(none)"}",
            $"detailState={detailState}",
            $"databaseError={FormatStateException(databaseError)}",
            $"journalError={FormatStateException(journalError)}",
            "Reconcile every immutable incident record and referenced sidecar before deleting this latch and restarting Miningcore.",
            string.Empty);
    }

    private static string FormatStateException(Exception error) => error == null
        ? "(none)"
        : SanitizeStateValue($"{error.GetType().FullName}: {error.Message}", 2048);

    private static string SanitizeStateValue(string value, int maxLength)
    {
        value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private ExactShareDetail WriteExactShareSidecar(string filename,
        IReadOnlyCollection<Share> shares)
    {
        var directory = Path.GetDirectoryName(filename)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(filename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var pools = new HashSet<string>(StringComparer.Ordinal);

            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var index = 0;

                foreach(var share in shares)
                {
                    ExactShareWriteCheckpoint(index++);
                    var record = Encoding.UTF8.GetBytes(SerializeExactShareRecord(share));
                    stream.Write(record);
                    hash.AppendData(record);

                    if(!string.IsNullOrWhiteSpace(share.PoolId))
                        pools.Add(share.PoolId);
                }

                stream.Flush(true);
            }

            File.Move(temporary, filename);
            DirectorySync(directory);

            return new ExactShareDetail(Convert.ToHexString(hash.GetHashAndReset()),
                pools.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // The startup latch is already durable and records an incomplete sidecar. A
                // stray temporary remains incident evidence rather than an acknowledgement.
            }
        }
    }

    private readonly record struct ExactShareDetail(string Hash, string[] Pools);

    private static string SerializeExactShareRecord(Share share)
    {
        var json = JsonConvert.SerializeObject(share, Formatting.None);
        return "shareJsonBase64=" + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(json)) + "\n";
    }

    private void WriteSmallStateFile(string filename, string content,
        bool replace)
    {
        var directory = Path.GetDirectoryName(filename)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(filename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using(var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024,
                leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, filename, replace);
            DirectorySync(directory);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // The destination is authoritative once renamed; otherwise the original write
                // error must escape while any temporary remains incident evidence.
            }
        }
    }

    private bool EnsureFatalStateDirectoryAccessible()
    {
        var directory = Path.GetDirectoryName(FatalStateFilename)!;
        DurableDirectory.EnsureCreated(directory, DirectorySync);

        // Opening the directory for enumeration catches access and metadata failures that
        // File.Exists would incorrectly collapse into "marker missing".
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Directory.EnumerateFileSystemEntries(directory)
            .Any(entry => string.Equals(Path.GetFullPath(entry),
                FatalStateFilename, comparison));
    }

    internal static string ResolveRecoveryFilename(ClusterConfig clusterConfig)
    {
        var configured = !string.IsNullOrWhiteSpace(clusterConfig.ShareRecoveryFile)
            ? clusterConfig.ShareRecoveryFile
            : "recovered-shares.txt";

        return Path.GetFullPath(configured);
    }

    internal static string ResolveStateDirectory(ClusterConfig clusterConfig)
    {
        if(!string.IsNullOrWhiteSpace(clusterConfig.ShareRecoveryStateDirectory))
            return Path.GetFullPath(clusterConfig.ShareRecoveryStateDirectory);

        var systemdStateDirectory = Environment.GetEnvironmentVariable("STATE_DIRECTORY");

        if(!string.IsNullOrWhiteSpace(systemdStateDirectory))
        {
            var first = OperatingSystem.IsWindows()
                ? systemdStateDirectory
                : systemdStateDirectory.Split(':', 2)[0];
            return Path.GetFullPath(first);
        }

        var applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if(!string.IsNullOrWhiteSpace(applicationData))
            return Path.Combine(applicationData, "Miningcore");

        return Path.Combine(AppContext.BaseDirectory, "state");
    }

    internal static string ComputeRecoveryPathHash(string recoveryFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        var identity = OperatingSystem.IsWindows()
            ? recoveryFilename.ToUpperInvariant()
            : recoveryFilename;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static void SyncDirectoryWhereSupported(string directory)
    {
        if(!OperatingSystem.IsLinux())
            return;

        const int oDirectory = 0x10000;
        var descriptor = open(directory, oDirectory);

        if(descriptor < 0)
            throw new IOException(
                $"Unable to open directory for durable sync (errno {Marshal.GetLastPInvokeError()})");

        try
        {
            if(fsync(descriptor) != 0)
                throw new IOException(
                    $"Unable to durably sync directory (errno {Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            _ = close(descriptor);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
