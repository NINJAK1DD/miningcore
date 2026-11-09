using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("blake3")]
public unsafe class Blake3IHash : IHashAlgorithm
{
    public byte[] dataKey { get; protected set; } = null;
    
    public Blake3IHash(byte[] dataKey = null)
    {
        ValidateKey(dataKey);
        this.dataKey = dataKey;
    }

    private static void ValidateKey(byte[] key)
    {
        if(!IsValidKeyLength(key))
            throw new ArgumentException("Blake3 keys must be empty (unkeyed) or exactly 32 bytes", nameof(dataKey));
    }

    private static bool IsValidKeyLength(byte[] key) => key is null or { Length: 0 or 32 };

    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        // The protected setter is retained for compatibility. Revalidate its value,
        // then pin and measure the same array even if a subclass replaces the property.
        var keyData = dataKey;
        if(!IsValidKeyLength(keyData))
            throw new InvalidOperationException("Blake3 key state must be empty (unkeyed) or exactly 32 bytes");

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                fixed (byte* key = keyData)
                {
                    var keyLength = keyData?.Length ?? 0;
                    Multihash.blake3(input, output, (uint) data.Length, key, (uint) keyLength);
                }
            }
        }
    }
}
