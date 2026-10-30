using System.Runtime.InteropServices;

namespace Miningcore.Tests.Util;

internal static class FloatingPointNativeProbe
{
    [DllImport("miningcore_test_fenv", EntryPoint = "miningcore_test_mxcsr_mask", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    internal static extern uint MxcsrMask();

    [DllImport("miningcore_test_fenv", EntryPoint = "miningcore_test_push_one", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    internal static extern void PushOne();

    [DllImport("miningcore_test_fenv", EntryPoint = "miningcore_test_pop_is_one", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    internal static extern int PopIsOne();
}
