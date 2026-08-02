using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Miningcore.Mining;

/// <summary>
/// Rejects a recovery journal whose final pathname is a link or whose inode/file identity has
/// multiple names. Adjacent ownership then remains intrinsic to the one accepted journal name,
/// including when its parent is reached through a directory symlink.
/// </summary>
internal static class RecoveryJournalPathSafety
{
    public static void EnsureSinglePhysicalNameIfExists(string filename)
    {
        filename = Path.GetFullPath(filename);
        using var handle = TryOpenNoFollow(filename);
        if(handle == null)
            return;

        var metadata = RecoveryJournalFileIdentity.ReadPhysicalMetadata(handle,
            filename);
        if(!metadata.IsRegularFile)
            throw new InvalidDataException(
                $"Recovery journal {filename} must be a regular file and not a symbolic link or reparse point");
        if(metadata.LinkCount != 1)
            throw new InvalidDataException(
                $"Recovery journal {filename} is a filesystem alias with {metadata.LinkCount} physical names. " +
                "Hard-linked recovery journals are not supported because ownership cannot be uniquely established.");
    }

    private static SafeFileHandle TryOpenNoFollow(string filename)
    {
        if(OperatingSystem.IsLinux())
        {
            const int oReadOnly = 0;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var descriptor = open(filename, oReadOnly | oNoFollow | oCloseOnExec);
            if(descriptor >= 0)
                return new SafeFileHandle(new IntPtr(descriptor), true);

            var error = Marshal.GetLastPInvokeError();
            if(error == 2) // ENOENT
                return null;

            throw new InvalidDataException(
                $"Recovery journal {filename} could not be opened without following links",
                new Win32Exception(error));
        }

        if(OperatingSystem.IsWindows())
        {
            const uint genericRead = 0x80000000;
            const uint shareRead = 0x00000001;
            const uint shareWrite = 0x00000002;
            const uint shareDelete = 0x00000004;
            const uint openExisting = 3;
            const uint openReparsePoint = 0x00200000;
            var result = CreateFile(filename, genericRead,
                shareRead | shareWrite | shareDelete, IntPtr.Zero, openExisting,
                openReparsePoint, IntPtr.Zero);
            if(!result.IsInvalid)
                return result;

            var error = Marshal.GetLastPInvokeError();
            result.Dispose();
            if(error is 2 or 3) // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
                return null;

            throw new InvalidDataException(
                $"Recovery journal {filename} could not be opened without following reparse points",
                new Win32Exception(error));
        }

        if(!File.Exists(filename))
            return null;
        if(new FileInfo(filename).LinkTarget != null)
            throw new InvalidDataException(
                $"Recovery journal {filename} must not be a symbolic link");

        return File.OpenHandle(filename, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string filename,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes,
        IntPtr templateFile);
}
