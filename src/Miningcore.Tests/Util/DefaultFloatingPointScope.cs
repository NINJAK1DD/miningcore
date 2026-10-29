using System;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

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
        [FieldOffset(0)] public ushort ControlWord;
        [FieldOffset(4)] public ushort StatusWord;
        [FieldOffset(8)] public ushort TagWord;
        [FieldOffset(12)] public uint InstructionPointer;
        [FieldOffset(16)] public ushort CodeSelector;
        [FieldOffset(18)] public ushort Opcode;
        [FieldOffset(20)] public uint DataPointer;
        [FieldOffset(24)] public ushort DataSelector;
        [FieldOffset(28)] public uint Mxcsr;

        // x87 instruction/data pointers and opcode record prior execution;
        // restoring numerical behavior does not require those values to match.
        internal bool HasSameBehavior(State other) => ControlWord == other.ControlWord &&
            StatusWord == other.StatusWord && TagWord == other.TagWord && Mxcsr == other.Mxcsr;

        // Sticky exception flags can change during ordinary arithmetic; they
        // are restoration state, but not evidence of a leaked numerical mode.
        internal bool HasSameControls(State other) => ControlWord == other.ControlWord &&
            (Mxcsr & ~0x3fu) == (other.Mxcsr & ~0x3fu);

        public override string ToString() =>
            $"x87(control=0x{ControlWord:x4}, status=0x{StatusWord:x4}, tags=0x{TagWord:x4}), MXCSR=0x{Mxcsr:x8}";
    }

    private State saved;
    private bool disposed;

    private DefaultFloatingPointScope(Action<string> diagnostic)
    {
        saved = Capture();
        // fenv_t stores tags/environment, not x87 register values. Resetting a
        // live x87 stack would exceed this scope's restoration contract.
        if(saved.TagWord != ushort.MaxValue)
            throw new InvalidOperationException("Numeric fixture isolation requires an empty x87 stack");
        try
        {
            // glibc defines FE_DFL_ENV as (const fenv_t *) -1.
            if(SetDefault(new IntPtr(-1)) != 0)
                throw new InvalidOperationException("Cannot establish the default floating-point environment");
            var current = Capture();
            if(!saved.HasSameControls(current))
                (diagnostic ?? Console.WriteLine)(
                    $"Non-default floating-point controls at numeric fixture entry on managed thread {Environment.CurrentManagedThreadId}. " +
                    $"Saved: {saved}; default: {current}. Fixture isolated; investigate preceding native calls (#226).");
        }
        catch(Exception failure)
        {
            // A capture or output callback failure must also roll back the reset.
            try { Restore(saved); }
            catch(Exception restoreFailure) { throw new AggregateException(failure, restoreFailure); }
            throw;
        }
    }

    internal static IDisposable Enter(Action<string> diagnostic = null) =>
        IsSupported ? new DefaultFloatingPointScope(diagnostic) : null;

    internal static void Run(Action action, Action<string> diagnostic = null) => Run(action, Enter(diagnostic));

    // Keep a failed assertion as well as a restoration failure; Dispose in a
    // plain using/finally would replace the original diagnostic.
    internal static void Run(Action action, IDisposable scope)
    {
        ExceptionDispatchInfo failure = null;
        try { action(); }
        catch(Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        try { scope?.Dispose(); }
        catch(Exception restoreFailure) when(failure != null)
        {
            throw new AggregateException("Fixture and floating-point restoration both failed",
                failure.SourceException, restoreFailure);
        }
        failure?.Throw();
    }

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
