using System;
using System.Runtime.InteropServices;

namespace Miningcore.Tests.Util;

// Numeric edge fixtures require gradual underflow. Native code can leave FTZ/DAZ
// enabled on a reused test thread; even a fresh thread inherits that state.
// This scope only isolates tests and never changes production hash execution.
internal sealed class DefaultFloatingPointScope : IDisposable
{
    internal static bool IsSupported => OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        !RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal);

    // glibc x86-64 bits/fenv.h: fenv_t is 32 bytes, including MXCSR at offset 28.
    // Keep this ABI-specific representation behind the platform guard above.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct State
    {
        [FieldOffset(28)] public uint Mxcsr;
    }

    private State saved;
    private bool disposed;

    private DefaultFloatingPointScope()
    {
        saved = Capture();
        // glibc defines FE_DFL_ENV as (const fenv_t *) -1.
        if(SetDefault(new IntPtr(-1)) != 0)
        {
            Restore(saved);
            throw new InvalidOperationException("Cannot establish the default floating-point environment");
        }
    }

    internal static IDisposable Enter() => IsSupported ? new DefaultFloatingPointScope() : null;

    internal static State Capture()
    {
        if(GetEnvironment(out var state) != 0)
            throw new InvalidOperationException("Cannot capture the floating-point environment");
        return state;
    }

    internal static void Restore(State state)
    {
        if(SetEnvironment(ref state) != 0)
            throw new InvalidOperationException("Cannot restore the floating-point environment");
    }

    public void Dispose()
    {
        if(disposed)
            return;
        Restore(saved);
        disposed = true;
    }

    [DllImport("libm.so.6", EntryPoint = "fegetenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetEnvironment(out State state);

    [DllImport("libm.so.6", EntryPoint = "fesetenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetEnvironment(ref State state);

    [DllImport("libm.so.6", EntryPoint = "fesetenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetDefault(IntPtr state);
}
