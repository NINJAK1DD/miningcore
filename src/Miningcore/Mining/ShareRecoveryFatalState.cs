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
        Exception databaseError, Exception journalError);
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
            MarkFatalCore(shareCount, pools, null, databaseError, journalError);
    }

    public void MarkFatalShares(IReadOnlyCollection<Share> shares,
        Exception databaseError, Exception journalError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var pools = shares.Select(x => x.PoolId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        lock(fatalStateGate)
            MarkFatalCore(shares.Count, pools, shares, databaseError,
                journalError);
    }

    private void MarkFatalCore(int shareCount, IReadOnlyCollection<string> pools,
        IReadOnlyCollection<Share> shares, Exception databaseError,
        Exception journalError)
    {
        _ = EnsureFatalStateDirectoryAccessible();
        var recoveryPathHash = ComputeRecoveryPathHash(RecoveryFilename);
        var incidentId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var previous = string.Empty;

        try
        {
            previous = File.ReadAllText(FatalStateFilename, new UTF8Encoding(false, true));
        }
        catch(FileNotFoundException)
        {
            // First incident for this recovery-journal identity.
        }

        var content = new StringBuilder(previous);

        if(content.Length == 0)
            content.AppendLine("Miningcore share-accounting durability failure");
        else if(content[content.Length - 1] != '\n')
            content.AppendLine();

        content
            .AppendLine("[incident]")
            .AppendLine($"incidentId={incidentId}")
            .AppendLine($"createdUtc={DateTimeOffset.UtcNow:O}")
            .AppendLine($"recoveryFile={RecoveryFilename}")
            .AppendLine($"recoveryPathSha256={recoveryPathHash}")
            .AppendLine($"shareCount={shareCount}")
            .AppendLine($"pools={string.Join(",", pools)}")
            .AppendLine($"databaseError={databaseError?.GetType().FullName}: {databaseError?.Message}")
            .AppendLine($"journalError={journalError?.GetType().FullName}: {journalError?.Message}")
            .AppendLine("Reconcile every incident before deleting this marker and restarting Miningcore.");

        if(shares != null)
        {
            content.AppendLine($"exactShareRecordCount={shares.Count}");

            foreach(var share in shares)
            {
                var json = JsonConvert.SerializeObject(share, Formatting.None);
                content.Append("shareJsonBase64=")
                    .AppendLine(Convert.ToBase64String(
                        new UTF8Encoding(false).GetBytes(json)));
            }
        }
        var directory = Path.GetDirectoryName(FatalStateFilename)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(FatalStateFilename)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using(var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using(var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024,
                leaveOpen: true))
            {
                writer.Write(content.ToString());
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, FatalStateFilename, true);
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
                // The final marker has already been atomically published or the caller receives
                // the original persistence failure. A stray temp file is not an acknowledgement.
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
