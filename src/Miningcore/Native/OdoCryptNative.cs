using System.Runtime.InteropServices;

namespace Miningcore.Native;

public static class OdoCryptNative
{
    [DllImport("libodocrypt", EntryPoint = "odocrypt_export",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int Hash(IntPtr input, IntPtr output, uint inputLength,
        uint key);
}
