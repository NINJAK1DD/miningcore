using Miningcore.Native;

namespace Miningcore.Crypto.Hashing;

internal static class Blake2bNativeCapability
{
    private static readonly Lazy<bool> keyedAvailable = new(() => IsKeyedAvailable(Hash));

    internal static bool IsKeyedAvailable() => keyedAvailable.Value;

    internal static void EnsureKeyedAvailable() => NativeHashCapability.EnsureAvailable(IsKeyedAvailable,
        "Keyed Blake2b requires blake2b_export passing keyed known-answer self-tests; this native runtime may ignore keys", null);

    // Call the native binding directly to avoid recursion through the managed guard.
    private static unsafe byte[] Hash(byte[] input)
    {
        var key = "BlockHash"u8.ToArray();
        var output = new byte[32];
        fixed(byte* inputPtr = input)
        fixed(byte* outputPtr = output)
        fixed(byte* keyPtr = key)
            Multihash.blake2b(inputPtr, outputPtr, (uint) input.Length, output.Length, keyPtr, (uint) key.Length);
        return output;
    }

    internal static bool IsKeyedAvailable(Func<byte[], byte[]> hash)
    {
        try
        {
            // Independent Python hashlib.blake2b vectors, key ASCII "BlockHash", digest_size=32.
            return hash(Array.Empty<byte>()).AsSpan().SequenceEqual(Convert.FromHexString(
                "a80b6aa20f20b15ebabe2b1949527f78a257594a732e774de637d85e6973a768")) &&
                hash(new byte[] { 0, 1, 2 }).AsSpan().SequenceEqual(Convert.FromHexString(
                "6f97f3b44764fcce84e8570a2c84ae99e68f4d2178ed826f04d7c20d051e3636"));
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
