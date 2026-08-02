using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Miningcore.Mining;

internal static class RecoveryStateFile
{
    internal const int MaximumBytes = 64 * 1024;
    internal const int MaximumLineCharacters = 16 * 1024;

    public static FileStream TryOpenExactEntry(string filename,
        Func<string, IEnumerable<string>> enumerateEntries,
        FileShare share = FileShare.Read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(enumerateEntries);

        filename = Path.GetFullPath(filename);
        var directory = Path.GetDirectoryName(filename)!;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string exactEntry = null;

        // Materialise enumeration so failures while advancing a lazy iterator are not mistaken
        // for a successfully proven absence.
        foreach(var entry in enumerateEntries(directory).ToArray())
        {
            if(string.Equals(Path.GetFullPath(entry), filename, comparison))
            {
                exactEntry = entry;
                break;
            }
        }

        if(exactEntry == null)
            return null;

        try
        {
            return OpenRegularFileNoFollow(exactEntry, filename, share);
        }
        catch(Exception ex) when(ex is FileNotFoundException or
                                  DirectoryNotFoundException)
        {
            throw new IOException(
                $"Recovery state entry {filename} disappeared after directory enumeration; " +
                "its absence cannot be proven", ex);
        }
    }

    private static FileStream OpenRegularFileNoFollow(string entry,
        string filename, FileShare share)
    {
        if(OperatingSystem.IsLinux())
            return OpenLinuxRegularFileNoFollow(entry, filename);

        if(OperatingSystem.IsWindows())
            return OpenWindowsRegularFileNoFollow(entry, filename, share);

        // Unsupported release hosts retain the managed pre-check and the post-read identity
        // verification. Supported Windows/Linux hosts use atomic no-follow handles below.
        var info = new FileInfo(entry);
        if(info.LinkTarget != null)
            throw new InvalidDataException(
                $"Recovery state entry {filename} is a symbolic link");
        var attributes = File.GetAttributes(entry);
        EnsureRegularAttributes(attributes, filename);
        return new FileStream(entry, FileMode.Open, FileAccess.Read,
            share, 4096, FileOptions.SequentialScan);
    }

    private static FileStream OpenLinuxRegularFileNoFollow(string entry,
        string filename)
    {
        const int oReadOnly = 0;
        const int oNonBlock = 0x800;
        const int oNoFollow = 0x20000;
        const int oCloseOnExec = 0x80000;
        const int atEmptyPath = 0x1000;
        const uint statxType = 0x0001;
        const ushort fileTypeMask = 0xF000;
        const ushort regularFile = 0x8000;
        const ushort directoryFile = 0x4000;

        var descriptor = open(entry,
            oReadOnly | oNonBlock | oNoFollow | oCloseOnExec, 0);
        if(descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if(error == 40) // ELOOP: O_NOFOLLOW rejected a symbolic link.
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is a symbolic link");

            throw new IOException(
                $"Unable to open recovery state entry {filename} without following links",
                new Win32Exception(error));
        }

        var handle = new SafeFileHandle(new IntPtr(descriptor), true);
        try
        {
            if(statx(descriptor, string.Empty, atEmptyPath, statxType,
                   out var info) != 0)
                throw new IOException(
                    $"Unable to inspect opened recovery state entry {filename}",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            var fileType = (ushort) (info.Mode & fileTypeMask);
            if(fileType == directoryFile)
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is a directory, not a regular file");
            if(fileType != regularFile)
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is not a regular file");

            return new FileStream(handle, FileAccess.Read, 4096, false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileStream OpenWindowsRegularFileNoFollow(string entry,
        string filename, FileShare share)
    {
        const uint genericRead = 0x80000000;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        const uint sequentialScan = 0x08000000;
        const uint fileTypeDisk = 1;

        var shareMode = (share & FileShare.Read) != 0 ? 0x00000001u : 0;
        if((share & FileShare.Write) != 0)
            shareMode |= 0x00000002;
        if((share & FileShare.Delete) != 0)
            shareMode |= 0x00000004;

        var handle = CreateFile(entry, genericRead, shareMode, IntPtr.Zero,
            openExisting, backupSemantics | openReparsePoint | sequentialScan,
            IntPtr.Zero);
        if(handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"Unable to open recovery state entry {filename} without following reparse points",
                new Win32Exception(error));
        }

        try
        {
            if(!GetFileInformationByHandleEx(handle, 9,
                   out var info, Marshal.SizeOf<FileAttributeTagInformation>()))
                throw new IOException(
                    $"Unable to inspect opened recovery state entry {filename}",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            EnsureRegularAttributes((FileAttributes) info.FileAttributes,
                filename);
            if(GetFileType(handle) != fileTypeDisk)
                throw new InvalidDataException(
                    $"Recovery state entry {filename} is not a regular disk file");

            return new FileStream(handle, FileAccess.Read, 4096, false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void EnsureRegularAttributes(FileAttributes attributes,
        string filename)
    {
        if((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException(
                $"Recovery state entry {filename} is a directory, not a regular file");
        if((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Recovery state entry {filename} is a symbolic link or unsupported reparse point");
    }

    public static string[] ReadAllLinesStable(FileStream stream,
        string filename, Func<string, IEnumerable<string>> enumerateEntries,
        Action<string> readCheckpoint = null,
        Action<int> lineReadCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        filename = Path.GetFullPath(filename);

        var identityBefore = RecoveryJournalFileIdentity.Read(stream);
        var lengthBefore = stream.Length;
        if(lengthBefore > MaximumBytes)
            throw new InvalidDataException(
                $"Recovery state file {filename} exceeds {MaximumBytes} bytes");

        var result = new List<string>();
        var accumulatedBytes = 0L;
        var strictUtf8 = new UTF8Encoding(false, true);
        using(var reader = new StreamReader(stream,
                  strictUtf8, false, 4096, leaveOpen: true))
        using(var lines = new BoundedLineReader(reader,
                  MaximumLineCharacters, $"Recovery state file {filename}"))
        {
            string line;
            while((line = lines.ReadLine()) != null)
            {
                lineReadCheckpoint?.Invoke(result.Count);
                accumulatedBytes += strictUtf8.GetByteCount(line) + 1L;
                if(accumulatedBytes > MaximumBytes)
                    throw new InvalidDataException(
                        $"Recovery state file {filename} exceeds {MaximumBytes} bytes while being read");

                result.Add(line);
            }
        }

        readCheckpoint?.Invoke(filename);

        var identityAfter = RecoveryJournalFileIdentity.Read(stream);
        var lengthAfter = stream.Length;
        if(identityAfter != identityBefore || lengthAfter != lengthBefore)
            throw new InvalidDataException(
                $"Recovery state file {filename} changed while it was being read");

        using var pathStream = TryOpenExactEntry(filename, enumerateEntries);
        if(pathStream == null ||
           RecoveryJournalFileIdentity.Read(pathStream) != identityAfter ||
           pathStream.Length != lengthAfter)
            throw new InvalidDataException(
                $"Recovery state path {filename} was replaced while it was being read");

        return result.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatxInformation
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint DeviceTypeMajor;
        public uint DeviceTypeMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(int directoryFileDescriptor,
        string path, int flags, uint mask, out StatxInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string filename,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int fileInformationClass,
        out FileAttributeTagInformation information, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
}
