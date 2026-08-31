using System.Runtime.InteropServices;

namespace Miningcore.Native;

public static unsafe class OdoCryptNative
{
    [DllImport("libodocrypt", EntryPoint = "odocrypt_export",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int Hash(byte* input, void* output, uint inputLength,
        uint key);
}
