using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Miningcore.Mining;

/// <summary>
/// Opens recovery boundary files without following their final pathname and rejects non-regular
/// objects or physical aliases. Linux metadata-only inspection uses O_PATH so FIFOs and device
/// entries cannot block startup while their type is being determined.
/// </summary>
internal static class RecoveryJournalPathSafety
{
    public static void EnsureSinglePhysicalNameIfExists(string filename)
    {
        filename = Path.GetFullPath(filename);
        using var handle = TryOpenPathNoFollow(filename);
        if(handle == null)
            return;

        EnsureRegularSingleName(handle, filename, "Recovery journal");
    }

    public static FileStream OpenJournalForWriteExisting(string filename)
    {
        return OpenRegularFileNoFollow(filename, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough,
            "Recovery journal");
    }

    public static FileStream OpenOwnershipFile(string filename)
    {
        return OpenRegularFileNoFollow(filename, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, FileOptions.None,
            "Recovery ownership file");
    }

    public static void EnsurePathStillIdentifiesFile(string filename,
        RecoveryJournalFileIdentity expectedIdentity, string description)
    {
        filename = Path.GetFullPath(filename);
        using var handle = TryOpenPathNoFollow(filename) ??
            throw new InvalidDataException(
                $"{description} {filename} disappeared while its ownership was held");
        EnsureRegularSingleName(handle, filename, description);

        if(RecoveryJournalFileIdentity.ReadStable(handle, filename) !=
           expectedIdentity)
            throw new InvalidDataException(
                $"{description} {filename} was replaced while its ownership was held");
    }

    internal static FileStream OpenRegularFileNoFollow(string filename,
        FileMode mode, FileAccess access, FileShare share, FileOptions options,
        string description)
    {
        filename = Path.GetFullPath(filename);
        SafeFileHandle handle;
        var create = mode is FileMode.OpenOrCreate or FileMode.CreateNew;

        if(OperatingSystem.IsLinux())
        {
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
            if(create)
                flags |= oCreate;
            if(mode == FileMode.CreateNew)
                flags |= oExclusive;
            if((options & FileOptions.WriteThrough) != 0)
                flags |= oDataSync;
            var descriptor = open(filename, flags, Convert.ToUInt32("600", 8));
            if(descriptor < 0)
                throw CreateOpenException(filename, description,
                    Marshal.GetLastPInvokeError(), create);

            handle = new SafeFileHandle(new IntPtr(descriptor), true);
        }
        else if(OperatingSystem.IsWindows())
        {
            const uint genericRead = 0x80000000;
            const uint genericWrite = 0x40000000;
            const uint shareRead = 0x00000001;
            const uint shareWrite = 0x00000002;
            const uint shareDelete = 0x00000004;
            const uint createNew = 1;
            const uint openExisting = 3;
            const uint openAlways = 4;
            const uint openReparsePoint = 0x00200000;
            const uint fileFlagWriteThrough = 0x80000000;
            const uint fileFlagOverlapped = 0x40000000;
            var flags = openReparsePoint |
                ((options & FileOptions.WriteThrough) != 0 ? fileFlagWriteThrough : 0) |
                ((options & FileOptions.Asynchronous) != 0 ? fileFlagOverlapped : 0);
            var desiredAccess = (access & FileAccess.Read) != 0 ? genericRead : 0;
            if((access & FileAccess.Write) != 0)
                desiredAccess |= genericWrite;
            var shareMode = ((share & FileShare.Read) != 0 ? shareRead : 0) |
                ((share & FileShare.Write) != 0 ? shareWrite : 0) |
                ((share & FileShare.Delete) != 0 ? shareDelete : 0);
            var disposition = mode == FileMode.CreateNew ? createNew :
                mode == FileMode.OpenOrCreate ? openAlways : openExisting;
            handle = CreateFile(filename, desiredAccess, shareMode,
                IntPtr.Zero, disposition, flags,
                IntPtr.Zero);
            if(handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw CreateOpenException(filename, description, error, create);
            }
        }
        else
        {
            if(new FileInfo(filename).LinkTarget != null)
                throw new InvalidDataException(
                    $"{description} {filename} must not be a symbolic link");
            handle = File.OpenHandle(filename,
                mode, access, share, options);
        }

        try
        {
            EnsureRegularSingleName(handle, filename, description);
            return new FileStream(handle, access, 4096,
                OperatingSystem.IsWindows() &&
                (options & FileOptions.Asynchronous) != 0);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle TryOpenPathNoFollow(string filename)
    {
        if(OperatingSystem.IsLinux())
        {
            const int oPath = 0x200000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var descriptor = open(filename, oPath | oNoFollow | oCloseOnExec, 0);
            if(descriptor >= 0)
                return new SafeFileHandle(new IntPtr(descriptor), true);

            var error = Marshal.GetLastPInvokeError();
            if(error == 2) // ENOENT
                return null;

            throw new InvalidDataException(
                $"Recovery path {filename} could not be inspected without following links",
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
            const uint backupSemantics = 0x02000000;
            var result = CreateFile(filename, genericRead,
                shareRead | shareWrite | shareDelete, IntPtr.Zero, openExisting,
                openReparsePoint | backupSemantics, IntPtr.Zero);
            if(!result.IsInvalid)
                return result;

            var error = Marshal.GetLastPInvokeError();
            result.Dispose();
            if(error is 2 or 3) // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
                return null;

            throw new InvalidDataException(
                $"Recovery path {filename} could not be inspected without following reparse points",
                new Win32Exception(error));
        }

        if(!File.Exists(filename) && !Directory.Exists(filename))
            return null;
        if(new FileInfo(filename).LinkTarget != null)
            throw new InvalidDataException(
                $"Recovery path {filename} must not be a symbolic link");

        return File.OpenHandle(filename, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
    }

    internal static void EnsureRegularSingleName(SafeFileHandle handle,
        string filename, string description)
    {
        var metadata = RecoveryJournalFileIdentity.ReadPhysicalMetadata(handle,
            filename);
        if(!metadata.IsRegularFile)
            throw new InvalidDataException(
                $"{description} {filename} must be a regular file and not a symbolic link, reparse point, directory, FIFO, socket, or device");
        if(metadata.LinkCount != 1)
            throw new InvalidDataException(
                $"{description} {filename} is a filesystem alias with {metadata.LinkCount} physical names. " +
                "Hard-linked recovery boundary files are not supported because ownership cannot be uniquely established.");
    }

    private static Exception CreateOpenException(string filename,
        string description, int error, bool create)
    {
        if((OperatingSystem.IsWindows() ? error == 3 : error == 2) &&
           !Directory.Exists(Path.GetDirectoryName(filename)))
            return new DirectoryNotFoundException(
                $"The recovery directory for {filename} does not exist");
        if(!create && (OperatingSystem.IsWindows() ? error is 2 or 3 : error == 2))
            return new FileNotFoundException(
                $"{description} {filename} does not exist", filename);
        if(OperatingSystem.IsWindows() && error is 32 or 33)
            return new IOException(
                $"{description} {filename} is already owned by another process",
                new Win32Exception(error));

        return new InvalidDataException(
            $"{description} {filename} could not be opened without following links or sharing its writer handle",
            new Win32Exception(error));
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
