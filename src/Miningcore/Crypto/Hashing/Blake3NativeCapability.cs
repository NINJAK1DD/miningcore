using Miningcore.Crypto.Hashing.Algorithms;

namespace Miningcore.Crypto.Hashing;

internal static class Blake3NativeCapability
{
    private static readonly Lazy<bool> available = new(() => IsAvailable(
        NativeHashCapability.HasExport,
        key => new Blake3IHash(key)));

    internal static bool IsAvailable() => available.Value;

    internal static void EnsureAvailable(string poolId, Func<bool> probe = null) =>
        NativeHashCapability.EnsureAvailable(probe ?? IsAvailable,
            "This pool requires a native runtime with blake3_export passing unkeyed and keyed known-answer self-tests", poolId);

    internal static bool IsAvailable(Func<string, bool> hasExport, Func<byte[], IHashAlgorithm> createHasher)
    {
        try
        {
            if(!hasExport("blake3_export")) return false;
            // Official BLAKE3 vectors, first 32 output bytes, input byte i % 251:
            // BLAKE3-team/BLAKE3 f55849f89cd85083c1c9daa2c5de20766f309c00/test_vectors/test_vectors.json
            // Cover block/chunk boundaries and wide SIMD batches on the actual startup CPU.
            // The additional 16385-byte case uses the same pattern, cross-checked independently
            // with BouncyCastle (it is not an entry in the pinned upstream vector file).
            var unkeyed = createHasher(null);
            var keyed = createHasher("whats the Elvish word for friend"u8.ToArray());
            foreach(var (length, hash, keyedHash) in vectors)
            {
                var input = new byte[length];
                for(var i = 0; i < input.Length; i++) input[i] = (byte) (i % 251);
                if(!Verify(unkeyed, input, hash) || !Verify(keyed, input, keyedHash)) return false;
            }
            return true;
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static readonly (int Length, string Hash, string KeyedHash)[] vectors =
    [
        (0, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262", "92b2b75604ed3c761f9d6f62392c8a9227ad0ea3f09573e783f1498a4ed60d26"),
        (3, "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f", "39e67b76b5a007d4921969779fe666da67b5213b096084ab674742f0d5ec62b9"),
        (64, "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f4c6dc7f2511b98", "ba8ced36f327700d213f120b1a207a3b8c04330528586f414d09f2f7d9ccb7e6"),
        (65, "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1c30ecb6b263dee", "c0a4edefa2d2accb9277c371ac12fcdbb52988a86edc54f0716e1591b4326e72"),
        (1024, "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7", "75c46f6f3d9eb4f55ecaaee480db732e6c2105546f1e675003687c31719c7ba4"),
        (1025, "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444", "357dc55de0c7e382c900fd6e320acc04146be01db6a8ce7210b7189bd664ea69"),
        (16385, "1dabe216be2578830263b049de1639f39f05a4da616b9b78c7a5e4e41662fd1f", "9f1e8750b485576aa8af42e403bfb340665b45a952799d0155756c994db7dfd2"),
    ];

    private static bool Verify(IHashAlgorithm hasher, ReadOnlySpan<byte> input, string expected)
    {
        Span<byte> output = stackalloc byte[32];
        output.Clear();
        hasher.Digest(input, output);
        return output.SequenceEqual(Convert.FromHexString(expected));
    }
}
