using System.Runtime.InteropServices;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Mining;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing;

internal static class Blake3NativeCapability
{
    // Keep this handle alive, matching the lifetime of the P/Invoke bindings.
    private static readonly Lazy<nint> library = new(() =>
        NativeLibrary.TryLoad("libmultihash", typeof(Multihash).Assembly, null, out var handle) ? handle : 0);
    private static readonly Lazy<bool> available = new(() => IsAvailable(
        name => library.Value != 0 && NativeLibrary.TryGetExport(library.Value, name, out _),
        key => new Blake3IHash(key)));

    internal static bool IsAvailable() => available.Value;

    internal static void EnsureAvailable(string poolId)
    {
        if(!IsAvailable())
            throw new TrustedPoolStartupException(
                "This pool requires a native runtime with blake3_export passing unkeyed and keyed known-answer self-tests", poolId);
    }

    internal static bool IsAvailable(Func<string, bool> hasExport, Func<byte[], IHashAlgorithm> createHasher)
    {
        try
        {
            if(!hasExport("blake3_export")) return false;
            // Official BLAKE3 vectors, first 32 output bytes, inputs empty and 00 01 02:
            // BLAKE3-team/BLAKE3 f55849f89cd85083c1c9daa2c5de20766f309c00/test_vectors/test_vectors.json
            // Two inputs reject constant/unwritten stubs; keyed vectors also reject ignored keys.
            var unkeyed = createHasher(null);
            var keyed = createHasher("whats the Elvish word for friend"u8.ToArray());
            return Verify(unkeyed, [], "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262") &&
                Verify(unkeyed, [0, 1, 2], "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f") &&
                Verify(keyed, [], "92b2b75604ed3c761f9d6f62392c8a9227ad0ea3f09573e783f1498a4ed60d26") &&
                Verify(keyed, [0, 1, 2], "39e67b76b5a007d4921969779fe666da67b5213b096084ab674742f0d5ec62b9");
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static bool Verify(IHashAlgorithm hasher, ReadOnlySpan<byte> input, string expected)
    {
        Span<byte> output = stackalloc byte[32];
        output.Clear();
        hasher.Digest(input, output);
        return output.SequenceEqual(Convert.FromHexString(expected));
    }
}
