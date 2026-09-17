using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Miningcore.Mining;

/// <summary>
/// Retains a physical recovery directory and performs recovery-boundary operations relative to
/// that retained object. A configured directory symlink may remain stable, but retargeting it
/// cannot redirect a later read, write, rename, delete, or directory sync to another directory.
/// </summary>
internal sealed class RecoveryDirectoryIdentity : IDisposable
{
    private RecoveryDirectoryIdentity(string path, SafeFileHandle handle,
        bool followFinalLink)
    {
        try
        {
            Path = path;
            this.handle = handle;
            this.followFinalLink = followFinalLink;
            var metadata = RecoveryJournalFileIdentity.ReadPhysicalMetadata(handle,
                path);
            if(!metadata.IsDirectory)
                throw new InvalidDataException(
                    $"Recovery directory {path} is not a directory or resolves to an unsupported reparse point");
            identity = RecoveryJournalFileIdentity.ReadStable(handle, path);
            physicalPath = OperatingSystem.IsWindows()
                ? GetPhysicalPath(handle, path)
                : path;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private readonly SafeFileHandle handle;
    private readonly RecoveryJournalFileIdentity identity;
    private readonly bool followFinalLink;
    private readonly string physicalPath;
    public string Path { get; }

    public static RecoveryDirectoryIdentity Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        return new RecoveryDirectoryIdentity(path,
            OpenDirectory(path, false), false);
    }

    public static RecoveryDirectoryIdentity OpenFollowingPath(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        return new RecoveryDirectoryIdentity(path,
            OpenDirectory(path, true), true);
    }

    public void EnsurePathStillIdentifiesDirectory()
    {
        using var current = OpenDirectory(Path, followFinalLink);
        if(RecoveryJournalFileIdentity.ReadStable(current, Path) != identity)
            throw new InvalidDataException(
                $"Recovery directory {Path} was replaced or retargeted during the operation");
    }

    public FileStream OpenEntry(string name, FileMode mode, FileAccess access,
        FileShare share, FileOptions options, string description)
    {
        name = ValidateEntryName(name);

        if(OperatingSystem.IsLinux())
        {
            // Unix open(2)/openat(2) has no FileShare equivalent. Cross-process exclusivity is
            // provided by the adjacent process-lifetime flock; FileShare remains meaningful on
            // Windows and is intentionally not treated as a Linux safety boundary.
            const int oWriteOnly = 1;
            const int oReadWrite = 2;
            const int oCreate = 0x40;
            const int oExclusive = 0x80;
            const int oNonBlock = 0x800;
            const int oDataSync = 0x1000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var flags = (access == FileAccess.Read ? 0 :
                    access == FileAccess.Write ? oWriteOnly : oReadWrite) |
                oNonBlock | oNoFollow | oCloseOnExec;
            if(mode is FileMode.OpenOrCreate or FileMode.CreateNew)
                flags |= oCreate;
            if(mode == FileMode.CreateNew)
                flags |= oExclusive;
            if((options & FileOptions.WriteThrough) != 0)
                flags |= oDataSync;

            var descriptor = openat(handle, name,
                flags, Convert.ToUInt32("600", 8));
            if(descriptor < 0)
                throw CreateEntryOpenException(name, description,
                    Marshal.GetLastPInvokeError(), mode);

            var entryHandle = new SafeFileHandle(new IntPtr(descriptor), true);
            try
            {
                RecoveryJournalPathSafety.EnsureRegularSingleName(entryHandle,
                    DisplayName(name), description);
                // A descriptor returned by openat is not tagged as asynchronous in SafeFileHandle.
                // FileStream's async APIs remain usable with its synchronous Unix strategy.
                return new FileStream(entryHandle, access, 4096, false);
            }
            catch
            {
                entryHandle.Dispose();
                throw;
            }
        }

        return RecoveryJournalPathSafety.OpenRegularFileNoFollow(
            System.IO.Path.Combine(physicalPath, name), mode, access, share,
            options, description);
    }

    public FileStream TryOpenEntry(string name, FileAccess access,
        FileShare share, FileOptions options, string description)
    {
        try
        {
            return OpenEntry(name, FileMode.Open, access, share, options,
                description);
        }
        catch(FileNotFoundException)
        {
            return null;
        }
    }

    public void EnsureEntryStillIdentifies(string name,
        RecoveryJournalFileIdentity expectedIdentity, string description)
    {
        using var stream = OpenEntry(name, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None,
            description);
        if(RecoveryJournalFileIdentity.ReadStable(stream) != expectedIdentity)
            throw new InvalidDataException(
                $"{description} {DisplayName(name)} was replaced while its ownership was held");
    }

    public void EnsureEntrySinglePhysicalNameIfExists(string name,
        string description)
    {
        name = ValidateEntryName(name);
        if(OperatingSystem.IsLinux())
        {
            const int oPath = 0x200000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var descriptor = openat(handle, name,
                oPath | oNoFollow | oCloseOnExec, 0);
            if(descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if(error == 2)
                    return;
                if(error == 13)
                    throw new UnauthorizedAccessException(
                        $"Permission was denied while inspecting recovery path {DisplayName(name)}",
                        new Win32Exception(error));
                throw new InvalidDataException(
                    $"Recovery path {DisplayName(name)} could not be inspected relative to its retained directory",
                    new Win32Exception(error));
            }

            using var entryHandle = new SafeFileHandle(new IntPtr(descriptor), true);
            RecoveryJournalPathSafety.EnsureRegularSingleName(entryHandle,
                DisplayName(name), description);
            return;
        }

        RecoveryJournalPathSafety.EnsureSinglePhysicalNameIfExists(
            System.IO.Path.Combine(physicalPath, name));
    }

    public void MoveEntry(string sourceName, string destinationName)
    {
        sourceName = ValidateEntryName(sourceName);
        destinationName = ValidateEntryName(destinationName);

        if(OperatingSystem.IsLinux())
        {
            const uint renameNoReplace = 1;
            int result;

            try
            {
                // SafeHandle marshalling pins the retained directory descriptor for the complete
                // native call, preventing concurrent Dispose from closing and recycling the fd.
                result = renameat2(handle, sourceName, handle, destinationName,
                    renameNoReplace);
            }
            catch(EntryPointNotFoundException)
            {
                MoveEntryUsingLinkFallback(sourceName, destinationName,
                    "the C library does not export renameat2");
                return;
            }

            if(result == 0)
                return;

            var error = Marshal.GetLastPInvokeError();
            if(IsRenameNoReplaceUnsupported(error))
            {
                MoveEntryUsingLinkFallback(sourceName, destinationName,
                    $"renameat2(RENAME_NOREPLACE) returned errno {error}");
                return;
            }

            throw new IOException(
                $"Unable to atomically rename recovery entry {DisplayName(sourceName)} to {DisplayName(destinationName)} without replacement",
                new Win32Exception(error));
        }

        File.Move(System.IO.Path.Combine(physicalPath, sourceName),
            System.IO.Path.Combine(physicalPath, destinationName), false);
    }

    public void DeleteEntry(string name)
    {
        name = ValidateEntryName(name);
        if(OperatingSystem.IsLinux())
        {
            var result = unlinkat(handle, name, 0);
            if(result != 0 && Marshal.GetLastPInvokeError() != 2)
                throw new IOException($"Unable to delete recovery entry {DisplayName(name)}",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }

        File.Delete(System.IO.Path.Combine(physicalPath, name));
    }

    public void Sync()
    {
        if(OperatingSystem.IsLinux() &&
           fsync(handle) != 0)
            throw new IOException($"Unable to durably sync recovery directory {Path}",
                new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    internal static bool IsRenameNoReplaceUnsupported(int error) =>
        // EPERM is deliberately excluded: it can mean either an unsupported filesystem flag or a
        // genuine permission/policy denial. Falling back on that ambiguous result could bypass a
        // security boundary, so Miningcore fails closed instead.
        error is 22 or 38 or 95; // EINVAL, ENOSYS, EOPNOTSUPP

    internal void MoveEntryUsingLinkFallback(string sourceName,
        string destinationName, string reason)
    {
        // linkat is an atomic no-replace publication: an existing destination makes it fail with
        // EEXIST. Removing the old name then completes the move. A crash between the calls can
        // leave two names for the same inode; Miningcore's single-link validation detects that
        // state and fails closed instead of trusting an ambiguous recovery boundary.
        if(linkat(handle, sourceName, handle, destinationName, 0) != 0)
        {
            var linkError = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to publish recovery entry {DisplayName(sourceName)} as {DisplayName(destinationName)} without replacement after {reason}",
                new Win32Exception(linkError));
        }

        if(unlinkat(handle, sourceName, 0) == 0)
            return;

        var sourceDeleteError = new Win32Exception(Marshal.GetLastPInvokeError());
        Exception destinationRollbackError = null;
        if(unlinkat(handle, destinationName, 0) != 0)
            destinationRollbackError = new Win32Exception(
                Marshal.GetLastPInvokeError());

        throw new IOException(
            $"Recovery entry {DisplayName(destinationName)} was linked without replacement after {reason}, but removing the source name failed",
            destinationRollbackError == null
                ? sourceDeleteError
                : new AggregateException(sourceDeleteError,
                    destinationRollbackError));
    }

    public void Dispose() => handle.Dispose();

    private string DisplayName(string name) => System.IO.Path.Combine(Path, name);

    private static string ValidateEntryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if(name is "." or ".." ||
           !string.Equals(name, System.IO.Path.GetFileName(name),
               StringComparison.Ordinal))
            throw new ArgumentException(
                "Recovery directory operations require a single filename", nameof(name));
        return name;
    }

    private static Exception CreateEntryOpenException(string name,
        string description, int error, FileMode mode)
    {
        if(error == 2 && mode == FileMode.Open)
            return new FileNotFoundException($"{description} {name} does not exist", name);
        if(error == 17 && mode == FileMode.CreateNew)
            return new IOException($"{description} {name} already exists",
                new Win32Exception(error));
        if(error == 13) // EACCES
            return new UnauthorizedAccessException(
                $"Permission was denied while opening {description} {name}",
                new Win32Exception(error));
        return new InvalidDataException(
            $"{description} {name} could not be opened relative to its retained recovery directory without following links",
            new Win32Exception(error));
    }

    private static SafeFileHandle OpenDirectory(string path,
        bool followFinalLink)
    {
        if(OperatingSystem.IsLinux())
        {
            const int oDirectory = 0x10000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var flags = oDirectory | oCloseOnExec;
            if(!followFinalLink)
                flags |= oNoFollow;
            var descriptor = open(path, flags, 0);
            if(descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if(error == 13)
                    throw new UnauthorizedAccessException(
                        $"Permission was denied while opening recovery archive directory {path}",
                        new Win32Exception(error));
                throw new InvalidDataException(
                    $"Recovery archive directory {path} could not be opened without following links",
                    new Win32Exception(error));
            }
            return new SafeFileHandle(new IntPtr(descriptor), true);
        }

        if(OperatingSystem.IsWindows())
        {
            const uint genericRead = 0x80000000;
            const uint shareRead = 0x00000001;
            const uint shareWrite = 0x00000002;
            const uint openExisting = 3;
            const uint backupSemantics = 0x02000000;
            const uint openReparsePoint = 0x00200000;
            var flags = backupSemantics | (followFinalLink ? 0 : openReparsePoint);
            // Omitting FILE_SHARE_DELETE pins the resolved directory against rename/removal while
            // its canonical path is used for child operations.
            var result = CreateFile(path, genericRead, shareRead | shareWrite,
                IntPtr.Zero, openExisting, flags, IntPtr.Zero);
            if(!result.IsInvalid)
                return result;
            var error = Marshal.GetLastPInvokeError();
            result.Dispose();
            if(error == 5)
                throw new UnauthorizedAccessException(
                    $"Permission was denied while opening recovery archive directory {path}",
                    new Win32Exception(error));
            throw new InvalidDataException(
                $"Recovery archive directory {path} could not be opened without following reparse points",
                new Win32Exception(error));
        }

        var info = new DirectoryInfo(path);
        if(!followFinalLink && info.LinkTarget != null)
            throw new InvalidDataException(
                $"Recovery archive directory {path} is a symbolic link");
        return File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, FileOptions.None);
    }

    private static string GetPhysicalPath(SafeFileHandle directory,
        string fallback)
    {
        if(!OperatingSystem.IsWindows())
            return fallback;
        var size = GetFinalPathNameByHandle(directory, null, 0, 0);
        if(size == 0)
            throw new IOException("Unable to resolve the retained recovery directory path",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        var buffer = new StringBuilder((int) size + 1);
        if(GetFinalPathNameByHandle(directory, buffer, (uint) buffer.Capacity, 0) == 0)
            throw new IOException("Unable to resolve the retained recovery directory path",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return buffer.ToString();
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int openat(SafeFileHandle directory, string path,
        int flags, uint mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat2(SafeFileHandle oldDirectory,
        string oldPath, SafeFileHandle newDirectory, string newPath,
        uint flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int linkat(SafeFileHandle oldDirectory,
        string oldPath, SafeFileHandle newDirectory, string newPath,
        int flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlinkat(SafeFileHandle directory,
        string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle descriptor);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string filename,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
        StringBuilder path, uint pathLength, uint flags);
}
