using System.Runtime.InteropServices;

namespace Miningcore.Native;

public static class OdoCryptNative
{
    // IntPtr is deliberate: the native-boundary tests pass null pointers to
    // prove the exported C ABI fails closed before managed pinning is involved.
    [DllImport("libodocrypt", EntryPoint = "odocrypt_export",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int Hash(IntPtr input, IntPtr output, uint inputLength,
        uint key);
}
