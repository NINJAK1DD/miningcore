using Miningcore.Native;

namespace Miningcore.Crypto.Hashing;

internal static class Blake2bNativeCapability
{
    private static readonly Lazy<bool> keyedAvailable = new(() => IsKeyedAvailable(Hash));

    internal static bool IsKeyedAvailable() => keyedAvailable.Value;

    internal const string Requirement = "Keyed Blake2b requires blake2b_export passing keyed known-answer self-tests; this native runtime may ignore keys";

    internal static void EnsureKeyedAvailable(string poolId, Func<bool> probe = null) =>
        NativeHashCapability.EnsureAvailable(probe ?? IsKeyedAvailable, Requirement, poolId);

    // Call the native binding directly to avoid recursion through the managed guard.
    private static unsafe byte[] Hash(byte[] input, int outputLength)
    {
        var key = "BlockHash"u8.ToArray();
        var output = new byte[outputLength];
        fixed(byte* inputPtr = input)
        fixed(byte* outputPtr = output)
        fixed(byte* keyPtr = key)
            Multihash.blake2b(inputPtr, outputPtr, (uint) input.Length, output.Length, keyPtr, (uint) key.Length);
        return output;
    }

    internal static bool IsKeyedAvailable(Func<byte[], int, byte[]> hash)
    {
        try
        {
            // Independent Python hashlib.blake2b vectors, key ASCII "BlockHash".
            return hash(Array.Empty<byte>(), 32).AsSpan().SequenceEqual(Convert.FromHexString(
                "a80b6aa20f20b15ebabe2b1949527f78a257594a732e774de637d85e6973a768")) &&
                hash(new byte[] { 0, 1, 2 }, 32).AsSpan().SequenceEqual(Convert.FromHexString(
                "6f97f3b44764fcce84e8570a2c84ae99e68f4d2178ed826f04d7c20d051e3636")) &&
                hash(new byte[] { 0, 1, 2 }, 64).AsSpan().SequenceEqual(Convert.FromHexString(
                "f8cd56827a7d04c63f128a77cdb9567f1b7341249c5806c260f146d08176bb03cd7214274fb9802a6d67767aa9addd5ae2c52cf27cb62eea5511d20fe9ad73ed"));
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
