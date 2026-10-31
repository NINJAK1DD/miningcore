using System.Runtime.InteropServices;
using Miningcore.Native;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;

namespace Miningcore.Blockchain.Xelis;

internal static class XelisNativeCapability
{
    // Keep the loaded runtime alive for the process, just like its P/Invoke bindings.
    private static readonly Lazy<nint> library = new(() =>
        NativeLibrary.TryLoad("libmultihash", typeof(Multihash).Assembly, null, out var handle) ? handle : 0);

    // Test once per process, before any pool can issue work. Exports alone do not
    // distinguish a working hasher from a stub or an unwritten output buffer.
    private static readonly Lazy<bool> available = new(() => IsAvailable(RuntimeInformation.ProcessArchitecture,
        name => library.Value != 0 && NativeLibrary.TryGetExport(library.Value, name, out _),
        new XelisHash(), new XelisHashV2()));

    internal static bool IsAvailable(Architecture architecture, Func<string, bool> hasExport,
        IHashAlgorithm v1, IHashAlgorithm v2)
    {
        try
        {
            return architecture == Architecture.X64 &&
                hasExport("xelis_hash_export") && hasExport("xelis_hash_v2_export") &&
                Verify(v1, "0ebbbd8a31edadfe098f2d770d84b719588675ab88a0a17067d00a8f36182265") &&
                Verify(v2, "7edb70f0748573902728a4691e9e2d7e4043ee34c823a11390d3d6e15fbe921b");
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    // xelis-project/xelis-hash f48262b931438fdbf2932711613e020c02f1ea04,
    // src/v1.rs and src/v2.rs test_zero_hash: 112 zero bytes of MinerWork.
    private static bool Verify(IHashAlgorithm hasher, string expected)
    {
        Span<byte> output = stackalloc byte[32];
        output.Clear();
        hasher.Digest(new byte[112], output);
        return output.SequenceEqual(Convert.FromHexString(expected));
    }

    internal static bool IsAvailable() => available.Value;
}
