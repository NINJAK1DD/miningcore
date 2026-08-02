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
    private RecoveryDirectoryIdentity(string path, SafeFileHandle handle)
    {
        Path = path;
        this.handle = handle;
        identity = RecoveryJournalFileIdentity.ReadStable(handle, path);
    }

    private readonly SafeFileHandle handle;
    private readonly RecoveryJournalFileIdentity identity;
    public string Path { get; }

    public static RecoveryDirectoryIdentity Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        return new RecoveryDirectoryIdentity(path, OpenNoFollow(path));
    }

    public void EnsurePathStillIdentifiesDirectory()
    {
        using var current = OpenNoFollow(Path);
        if(RecoveryJournalFileIdentity.ReadStable(current, Path) != identity)
            throw new InvalidDataException(
                $"Recovery archive directory {Path} was replaced during source retirement");
    }

    public void Dispose() => handle.Dispose();

    private static SafeFileHandle OpenNoFollow(string path)
    {
        if(OperatingSystem.IsLinux())
        {
            const int oReadOnly = 0;
            const int oDirectory = 0x10000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var descriptor = open(path,
                oReadOnly | oDirectory | oNoFollow | oCloseOnExec, 0);
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
            var result = CreateFile(path, genericRead,
                shareRead | shareWrite | shareDelete, IntPtr.Zero, openExisting,
                backupSemantics | openReparsePoint, IntPtr.Zero);
            if(result.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                result.Dispose();
                throw new InvalidDataException(
                    $"Recovery archive directory {path} could not be opened without following reparse points",
                    new Win32Exception(error));
            }

            var attributes = File.GetAttributes(path);
            if((attributes & FileAttributes.Directory) == 0 ||
               (attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.Dispose();
                throw new InvalidDataException(
                    $"Recovery archive directory {path} is not a regular directory or is a reparse point");
            }

            return result;
        }

        var info = new DirectoryInfo(path);
        if(info.LinkTarget != null)
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
