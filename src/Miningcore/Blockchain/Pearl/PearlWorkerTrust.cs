using System.IO;
using System.Runtime.InteropServices;

namespace Miningcore.Blockchain.Pearl;

internal static class PearlWorkerTrust
{
    // Linux statx has a fixed 256-byte ABI, unlike architecture-dependent stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint Owner;
        [FieldOffset(28)] internal ushort Mode;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(int directory, string path, int flags, uint mask, out Statx result);
    [DllImport("libc")]
    private static extern uint geteuid();

    internal static bool TrustedOwner(uint owner, uint service) => owner == 0 || owner == service;

    internal static void Validate(string executable)
    {
        var service = geteuid();
        var file = true;
        for(var path = Path.GetFullPath(executable); path != null; path = Path.GetDirectoryName(path))
        {
            const uint requested = 0x000b; // type, mode and UID
            if(statx(-100, path, 0x100, requested, out var info) != 0 || (info.Mask & requested) != requested ||
                !TrustedOwner(info.Owner, service) || (info.Mode & 0xf000) != (file ? 0x8000 : 0x4000) ||
                (info.Mode & 0x12) != 0 && (file || (info.Mode & 0x200) == 0))
                throw new InvalidDataException("Pearl worker and its ancestors require trusted ownership, safe permissions and no symlinks");
            // A root/service-owned sticky directory protects its trusted child's
            // directory entry (for example /tmp); ordinary writable parents do not.
            file = false;
        }
    }
}
