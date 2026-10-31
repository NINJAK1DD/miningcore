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
            if(architecture != Architecture.X64 ||
                !hasExport("xelis_hash_export") || !hasExport("xelis_hash_v2_export"))
                return false;

            var v1Input = new byte[112];
            "xelis-hashing-algorithm"u8.CopyTo(v1Input);
            var v2Input = Convert.FromHexString(
                "acec6cd4b51f6d2d2cf236e18f85592cb36c27bf2074e5213f822178b959928d" +
                "0a4fb76bee7a5cde19865a6b746eec35ff05d67e18d861c794effd66c7b8e8fd" +
                "9e9156bb70514e46506e21259fe9c601b26cd2646d9b6a7c7c535932c573e7204a" +
                "025c2f19dc87f97aacdc898fea44bc");
            return Verify(v1, "0ebbbd8a31edadfe098f2d770d84b719588675ab88a0a17067d00a8f36182265") &&
                Verify(v1, "6a6aad08cf3b766cb0c4097cfac3033d1e92eeb6585373518b38031cb0564415", v1Input) &&
                Verify(v2, "7edb70f0748573902728a4691e9e2d7e4043ee34c823a11390d3d6e15fbe921b") &&
                Verify(v2, "c7729a1c04a4c4b2751194cb7de43391a2de6acacd37f4b25e1df8f262dd9eb3", v2Input);
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    // xelis-project/xelis-hash f48262b931438fdbf2932711613e020c02f1ea04,
    // v1 test_zero_input/test_xelis_input and v2 test_zero_hash/test_verify_output.
    // Nonzero vectors also reject wrappers that ignore the caller's input.
    private static bool Verify(IHashAlgorithm hasher, string expected, byte[] input = null)
    {
        Span<byte> output = stackalloc byte[32];
        output.Clear();
        hasher.Digest(input ?? new byte[112], output);
        return output.SequenceEqual(Convert.FromHexString(expected));
    }

    internal static bool IsAvailable() => available.Value;
}
