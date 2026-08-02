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
        filename = Path.GetFullPath(filename);
        var handle = OpenRegularFileNoFollow(filename, false, true,
            "Recovery journal");

        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, 4096, false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static FileStream OpenOwnershipFile(string filename)
    {
        filename = Path.GetFullPath(filename);
        var handle = OpenRegularFileNoFollow(filename, true, false,
            "Recovery ownership file");

        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, 4096, false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
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

    private static SafeFileHandle OpenRegularFileNoFollow(string filename,
        bool create, bool writeThrough, string description)
    {
        SafeFileHandle handle;

        if(OperatingSystem.IsLinux())
        {
            const int oReadWrite = 2;
            const int oCreate = 0x40;
            const int oNonBlock = 0x800;
            const int oDataSync = 0x1000;
            const int oNoFollow = 0x20000;
            const int oCloseOnExec = 0x80000;
            var flags = oReadWrite | oNonBlock | oNoFollow | oCloseOnExec;
            if(create)
                flags |= oCreate;
            if(writeThrough)
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
            const uint openExisting = 3;
            const uint openAlways = 4;
            const uint openReparsePoint = 0x00200000;
            const uint fileFlagWriteThrough = 0x80000000;
            var flags = openReparsePoint |
                (writeThrough ? fileFlagWriteThrough : 0);
            handle = CreateFile(filename, genericRead | genericWrite, 0,
                IntPtr.Zero, create ? openAlways : openExisting, flags,
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
                create ? FileMode.OpenOrCreate : FileMode.Open,
                FileAccess.ReadWrite, FileShare.None,
                writeThrough ? FileOptions.WriteThrough : FileOptions.None);
        }

        try
        {
            EnsureRegularSingleName(handle, filename, description);
            return handle;
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

    private static void EnsureRegularSingleName(SafeFileHandle handle,
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
