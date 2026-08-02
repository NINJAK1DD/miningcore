using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Miningcore.Mining;

internal readonly record struct RecoveryJournalFileIdentity(string Value)
{
    public static RecoveryJournalFileIdentity Read(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if(OperatingSystem.IsWindows())
            return ReadWindows(stream.SafeFileHandle);

        if(OperatingSystem.IsLinux())
            return ReadLinux(stream.SafeFileHandle);

        // FileShare.Read prevents replacement by managed writers while this handle is open.
        // Platforms other than the supported Windows/Linux release hosts retain a conservative
        // metadata identity so an ordinary replacement is still detected.
        var info = new FileInfo(stream.Name);
        return new RecoveryJournalFileIdentity(
            $"metadata:{info.FullName}:{info.CreationTimeUtc.Ticks}");
    }

    public static RecoveryJournalFileIdentity ReadStable(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if(OperatingSystem.IsWindows())
            return ReadWindows(stream.SafeFileHandle);

        if(OperatingSystem.IsLinux())
            return ReadLinux(stream.SafeFileHandle, false);

        // Cross-rename stable identity is required only on the supported Windows/Linux release
        // hosts. Other platforms retain their conservative metadata identity and fail closed if
        // that identity cannot survive the rename.
        return Read(stream);
    }

    internal static RecoveryJournalFileIdentity ReadStable(
        SafeFileHandle handle, string fallbackPath)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if(OperatingSystem.IsWindows())
            return ReadWindows(handle);
        if(OperatingSystem.IsLinux())
            return ReadLinux(handle, false);

        var info = new DirectoryInfo(fallbackPath);
        return new RecoveryJournalFileIdentity(
            $"metadata:{info.FullName}:{info.CreationTimeUtc.Ticks}");
    }

    internal static RecoveryJournalPhysicalMetadata ReadPhysicalMetadata(
        SafeFileHandle handle, string fallbackPath)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if(OperatingSystem.IsWindows())
        {
            if(!GetFileInformationByHandle(handle, out var info))
                throw new IOException("Unable to inspect recovery journal link identity",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            const uint directoryAttribute = 0x10;
            const uint reparsePointAttribute = 0x400;
            var isDirectory = (info.FileAttributes & directoryAttribute) != 0;
            var isReparsePoint =
                (info.FileAttributes & reparsePointAttribute) != 0;
            return new RecoveryJournalPhysicalMetadata(info.NumberOfLinks,
                !isDirectory && !isReparsePoint,
                isDirectory && !isReparsePoint);
        }

        if(OperatingSystem.IsLinux())
        {
            const int atEmptyPath = 0x1000;
            const uint statxBasicStats = 0x07ff;
            const ushort fileTypeMask = 0xf000;
            const ushort regularFile = 0x8000;
            const ushort directory = 0x4000;

            if(statx(handle.DangerousGetHandle().ToInt32(), string.Empty,
                   atEmptyPath, statxBasicStats, out var info) != 0)
                throw new IOException("Unable to inspect recovery journal link identity",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            var fileType = (ushort) (info.Mode & fileTypeMask);
            return new RecoveryJournalPhysicalMetadata(info.LinkCount,
                fileType == regularFile, fileType == directory);
        }

        var fallback = new FileInfo(fallbackPath);
        var isFallbackDirectory =
            (fallback.Attributes & FileAttributes.Directory) != 0;
        return new RecoveryJournalPhysicalMetadata(1,
            fallback.LinkTarget == null && !isFallbackDirectory,
            fallback.LinkTarget == null && isFallbackDirectory);
    }

    private static RecoveryJournalFileIdentity ReadWindows(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Unable to read recovery journal file identity",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        var index = ((ulong) info.FileIndexHigh << 32) | info.FileIndexLow;
        return new RecoveryJournalFileIdentity(
            $"windows:{info.VolumeSerialNumber:X8}:{index:X16}");
    }

    private static RecoveryJournalFileIdentity ReadLinux(SafeFileHandle handle,
        bool includeChangeTime = true)
    {
        // statx has a fixed cross-architecture layout and exposes the file birth timestamp.
        // The birth timestamp distinguishes a delete/recreate replacement even when Linux
        // immediately recycles the same inode number for a same-length file.
        const int atEmptyPath = 0x1000;
        const uint statxInode = 0x0100;
        const uint statxBirthTime = 0x0800;
        const uint statxBasicStats = 0x07ff;

        if(statx(handle.DangerousGetHandle().ToInt32(), string.Empty,
               atEmptyPath, statxBasicStats | statxBirthTime, out var info) != 0)
            throw new IOException("Unable to read recovery journal file identity",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        if((info.Mask & statxInode) == 0)
            throw new IOException(
                "Linux did not return an inode for the recovery journal file");

        var birth = (info.Mask & statxBirthTime) != 0
            ? $"{info.BirthTime.Seconds:X16}:{info.BirthTime.Nanoseconds:X8}"
            : "unavailable";
        var stable =
            $"linux:{info.DeviceMajor:X8}:{info.DeviceMinor:X8}:" +
            $"{info.Inode:X16}:{birth}";
        return new RecoveryJournalFileIdentity(includeChangeTime
            ? stable + $":{info.ChangeTime.Seconds:X16}:{info.ChangeTime.Nanoseconds:X8}"
            : stable);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,
        out ByHandleFileInformation information);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(int directoryFileDescriptor,
        string path, int flags, uint mask, out StatxInformation information);
}

internal readonly record struct RecoveryJournalPhysicalMetadata(
    uint LinkCount, bool IsRegularFile, bool IsDirectory);
