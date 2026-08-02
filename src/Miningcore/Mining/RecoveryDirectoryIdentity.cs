using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Miningcore.Mining;

/// <summary>
/// Holds a no-follow handle to a recovery directory and verifies that its pathname continues to
/// identify the same directory object across destructive recovery-retirement steps.
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
    public string Path { get; }

    public static RecoveryDirectoryIdentity Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        return new RecoveryDirectoryIdentity(path,
            OpenDirectory(path, false), false);
    }

    /// <summary>
    /// Retains the physical identity reached through a configured directory pathname. This allows
    /// a stable directory symlink while detecting replacement or retargeting before later writes.
    /// </summary>
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

    public void Dispose() => handle.Dispose();

    private static SafeFileHandle OpenDirectory(string path,
        bool followFinalLink)
    {
        if(OperatingSystem.IsLinux())
        {
            const int oReadOnly = 0;
            const int oDirectory = 0x10000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var flags = oReadOnly | oDirectory | oCloseOnExec;
            if(!followFinalLink)
                flags |= oNoFollow;
            var descriptor = open(path, flags, 0);
            if(descriptor < 0)
                throw new InvalidDataException(
                    $"Recovery archive directory {path} could not be opened without following links",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            return new SafeFileHandle(new IntPtr(descriptor), true);
        }

        if(OperatingSystem.IsWindows())
        {
            const uint genericRead = 0x80000000;
            const uint shareRead = 0x00000001;
            const uint shareWrite = 0x00000002;
            const uint shareDelete = 0x00000004;
            const uint openExisting = 3;
            const uint backupSemantics = 0x02000000;
            const uint openReparsePoint = 0x00200000;
            var flags = backupSemantics;
            if(!followFinalLink)
                flags |= openReparsePoint;
            var result = CreateFile(path, genericRead,
                shareRead | shareWrite | shareDelete, IntPtr.Zero, openExisting,
                flags, IntPtr.Zero);
            if(result.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                result.Dispose();
                throw new InvalidDataException(
                    $"Recovery archive directory {path} could not be opened without following reparse points",
                    new Win32Exception(error));
            }

            return result;
        }

        var info = new DirectoryInfo(path);
        if(!followFinalLink && info.LinkTarget != null)
            throw new InvalidDataException(
                $"Recovery archive directory {path} is a symbolic link");
        return File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, uint mode);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string filename,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes,
        IntPtr templateFile);
}
