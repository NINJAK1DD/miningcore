using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Miningcore.Configuration;

namespace Miningcore.Mining;

public interface IShareRecoveryPathOwnership : IDisposable
{
    string RecoveryFilename { get; }
    string OwnershipFilename { get; }
    bool IsHeld { get; }
    void Acquire();
    void EnsureJournalPathIsExclusive();
    FileStream OpenRecoveryEntry(string filename, FileMode mode,
        FileAccess access, FileShare share, FileOptions options,
        string description);
    FileStream TryOpenRecoveryEntry(string filename, FileAccess access,
        FileShare share, FileOptions options, string description);
    void MoveRecoveryEntry(string sourceFilename, string destinationFilename);
    void DeleteRecoveryEntry(string filename);
    void SyncRecoveryDirectory();
    void Release();
}

/// <summary>
/// Holds exclusive process-lifetime ownership of one configured recovery journal. This is a
/// separate boundary from the short-lived fatal-state mutation lock: it prevents two Miningcore
/// processes from mining into, importing, or retiring the same journal concurrently.
/// </summary>
public sealed class ShareRecoveryPathOwnership : IShareRecoveryPathOwnership
{
    public ShareRecoveryPathOwnership(ClusterConfig clusterConfig) :
        this(ShareRecoveryFatalState.ResolveRecoveryFilename(clusterConfig))
    {
    }

    internal ShareRecoveryPathOwnership(string recoveryFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);

        RecoveryFilename = Path.GetFullPath(recoveryFilename);
        var directory = Path.GetDirectoryName(RecoveryFilename)!;
        var nameHash = ShareRecoveryFatalState.ComputeRecoveryPathHash(
            Path.GetFileName(RecoveryFilename));
        OwnershipFilename = Path.Combine(directory,
            $".miningcore-share-recovery-{nameHash}.owner.lock");
    }

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private readonly object gate = new();
    private FileStream ownershipStream;
    private RecoveryDirectoryIdentity recoveryDirectoryIdentity;
    private RecoveryJournalFileIdentity ownershipIdentity;
    internal Action<string> DirectoryOperationCheckpoint { get; set; } = _ => { };

    public string RecoveryFilename { get; }
    public string OwnershipFilename { get; }
    public bool IsHeld
    {
        get
        {
            lock(gate)
                return ownershipStream != null;
        }
    }

    public void Acquire()
    {
        lock(gate)
        {
            if(ownershipStream != null)
                return;

            var directory = Path.GetDirectoryName(OwnershipFilename)!;
            DurableDirectory.EnsureCreated(directory,
                ShareRecoveryFatalState.SyncDirectoryWhereSupported);
            FileStream stream = null;
            RecoveryDirectoryIdentity directoryIdentity = null;

            try
            {
                directoryIdentity = RecoveryDirectoryIdentity.OpenFollowingPath(
                    directory);
                stream = directoryIdentity.OpenEntry(
                    Path.GetFileName(OwnershipFilename), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, FileOptions.None,
                    "Recovery ownership file");

                if(!OperatingSystem.IsWindows())
                    AcquireUnixLock(stream.SafeFileHandle);

                var identity = RecoveryJournalFileIdentity.ReadStable(stream);
                recoveryDirectoryIdentity = directoryIdentity;
                ownershipIdentity = identity;
                ownershipStream = stream;
                directoryIdentity = null;
                stream = null;
            }
            catch(Exception ex)
            {
                stream?.Dispose();
                directoryIdentity?.Dispose();
                if(ex is IOException or UnauthorizedAccessException or Win32Exception)
                    throw new IOException(
                        $"Another Miningcore process owns recovery journal {RecoveryFilename}, " +
                        $"or exclusive ownership could not be established using {OwnershipFilename}. " +
                        "Run exactly one local share recorder, merged-mining relay submitter or " +
                        "recovery import per recovery path.", ex);
                throw;
            }

            try
            {
                EnsureJournalPathIsExclusive();
            }
            catch
            {
                Release();
                throw;
            }
        }
    }

    public void EnsureJournalPathIsExclusive()
    {
        lock(gate)
        {
            if(ownershipStream == null)
                throw new InvalidOperationException(
                    $"Recovery journal ownership is not held for {RecoveryFilename}");

            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
            if(!OperatingSystem.IsWindows())
                recoveryDirectoryIdentity.EnsureEntryStillIdentifies(
                    Path.GetFileName(OwnershipFilename), ownershipIdentity,
                    "Recovery ownership file");
            recoveryDirectoryIdentity.EnsureEntrySinglePhysicalNameIfExists(
                Path.GetFileName(RecoveryFilename), "Recovery journal");
        }
    }

    public FileStream OpenRecoveryEntry(string filename, FileMode mode,
        FileAccess access, FileShare share, FileOptions options,
        string description)
    {
        lock(gate)
        {
            EnsureHeld();
            var name = GetEntryName(filename);
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
            DirectoryOperationCheckpoint($"open:{name}");
            var stream = recoveryDirectoryIdentity.OpenEntry(name, mode, access,
                share, options, description);
            try
            {
                recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    public FileStream TryOpenRecoveryEntry(string filename, FileAccess access,
        FileShare share, FileOptions options, string description)
    {
        lock(gate)
        {
            EnsureHeld();
            var name = GetEntryName(filename);
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
            DirectoryOperationCheckpoint($"try-open:{name}");
            var stream = recoveryDirectoryIdentity.TryOpenEntry(name, access,
                share, options, description);
            try
            {
                recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
                return stream;
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    public void MoveRecoveryEntry(string sourceFilename,
        string destinationFilename)
    {
        lock(gate)
        {
            EnsureHeld();
            var source = GetEntryName(sourceFilename);
            var destination = GetEntryName(destinationFilename);
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
            DirectoryOperationCheckpoint($"move:{source}:{destination}");
            recoveryDirectoryIdentity.MoveEntry(source, destination);
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
        }
    }

    public void DeleteRecoveryEntry(string filename)
    {
        lock(gate)
        {
            EnsureHeld();
            var name = GetEntryName(filename);
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
            DirectoryOperationCheckpoint($"delete:{name}");
            recoveryDirectoryIdentity.DeleteEntry(name);
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
        }
    }

    public void SyncRecoveryDirectory()
    {
        lock(gate)
        {
            EnsureHeld();
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
            DirectoryOperationCheckpoint("sync");
            recoveryDirectoryIdentity.Sync();
            recoveryDirectoryIdentity.EnsurePathStillIdentifiesDirectory();
        }
    }

    public void Release()
    {
        lock(gate)
        {
            var stream = ownershipStream;
            if(stream == null)
                return;

            ownershipStream = null;
            var directoryIdentity = recoveryDirectoryIdentity;
            recoveryDirectoryIdentity = null;
            ownershipIdentity = default;
            if(!OperatingSystem.IsWindows())
                _ = flock(stream.SafeFileHandle, LockUnlock);
            stream.Dispose();
            directoryIdentity?.Dispose();
        }
    }

    public void Dispose() => Release();

    private void EnsureHeld()
    {
        if(ownershipStream == null)
            throw new InvalidOperationException(
                $"Recovery journal ownership is not held for {RecoveryFilename}");
    }

    private string GetEntryName(string filename)
    {
        filename = Path.GetFullPath(filename);
        var expectedDirectory = Path.GetDirectoryName(RecoveryFilename)!;
        var actualDirectory = Path.GetDirectoryName(filename)!;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if(!string.Equals(expectedDirectory, actualDirectory, comparison))
            throw new InvalidOperationException(
                $"Recovery entry {filename} is outside the owned directory {expectedDirectory}");
        return Path.GetFileName(filename);
    }

    private static void AcquireUnixLock(SafeFileHandle handle)
    {
        if(flock(handle, LockExclusive | LockNonBlocking) == 0)
            return;

        throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(SafeFileHandle fd, int operation);
}
