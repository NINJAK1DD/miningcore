using System.Runtime.InteropServices;
using Miningcore.Mining;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing;

internal static class NativeHashCapability
{
    // Share one retained handle for the lifetime of the P/Invoke bindings.
    private static readonly Lazy<nint> library = new(() =>
        NativeLibrary.TryLoad("libmultihash", typeof(Multihash).Assembly, null, out var handle) ? handle : 0);

    internal static bool HasExport(string name) =>
        library.Value != 0 && NativeLibrary.TryGetExport(library.Value, name, out _);

    internal static void EnsureAvailable(Func<bool> probe, string requirement, string poolId)
    {
        bool available;
        try
        {
            available = probe();
        }
        catch(Exception ex) when(ex is not (OutOfMemoryException or StackOverflowException or
            AccessViolationException or SEHException or OperationCanceledException))
        {
            // Lazy may cache a managed probe failure. Wrap it separately for each pool;
            // never expose exception messages, paths or arbitrary native diagnostics.
            throw new TrustedPoolStartupException(requirement + "; unexpected managed validation failure",
                poolId, ex);
        }

        if(!available)
            throw new TrustedPoolStartupException(requirement, poolId);
    }
}
