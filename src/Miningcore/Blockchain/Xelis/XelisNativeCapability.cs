using System.Runtime.InteropServices;
using Miningcore.Native;

namespace Miningcore.Blockchain.Xelis;

internal static class XelisNativeCapability
{
    // Keep the loaded runtime alive for the process, just like its P/Invoke bindings.
    private static readonly Lazy<nint> library = new(() =>
        NativeLibrary.TryLoad("libmultihash", typeof(Multihash).Assembly, null, out var handle) ? handle : 0);

    internal static bool IsAvailable(Architecture architecture, Func<string, bool> hasExport) =>
        architecture == Architecture.X64 && hasExport("xelis_hash_export") && hasExport("xelis_hash_v2_export");

    internal static bool IsAvailable() => IsAvailable(RuntimeInformation.ProcessArchitecture,
        name => library.Value != 0 && NativeLibrary.TryGetExport(library.Value, name, out _));
}
