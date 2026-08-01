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

    private static RecoveryJournalFileIdentity ReadWindows(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Unable to read recovery journal file identity",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        var index = ((ulong) info.FileIndexHigh << 32) | info.FileIndexLow;
        return new RecoveryJournalFileIdentity(
            $"windows:{info.VolumeSerialNumber:X8}:{index:X16}");
    }

    private static RecoveryJournalFileIdentity ReadLinux(SafeFileHandle handle)
    {
        // Linux stat begins with st_dev and st_ino on every architecture supported by the
        // published Miningcore packages. Allocate beyond the native structure size so libc can
        // populate it without binding the managed code to the remainder of the ABI layout.
        var buffer = Marshal.AllocHGlobal(256);

        try
        {
            if(fstat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
                throw new IOException("Unable to read recovery journal file identity",
                    new Win32Exception(Marshal.GetLastPInvokeError()));

            var device = unchecked((ulong) Marshal.ReadInt64(buffer, 0));
            var inode = unchecked((ulong) Marshal.ReadInt64(buffer, 8));
            return new RecoveryJournalFileIdentity(
                $"linux:{device:X16}:{inode:X16}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,
        out ByHandleFileInformation information);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int descriptor, IntPtr buffer);
}
