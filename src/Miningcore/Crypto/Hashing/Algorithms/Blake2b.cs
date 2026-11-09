using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("blake2b")]
public unsafe class Blake2b : IHashAlgorithm
{
    public byte[] dataKey { get; protected set; } = null;

    internal Func<bool> KeyedCapabilityProbe { get; init; } = Blake2bNativeCapability.IsKeyedAvailable;
    
    public Blake2b(byte[] dataKey = null)
    {
        if(dataKey is { Length: > 64 })
            throw new ArgumentException("Blake2b keys must contain at most 64 bytes", nameof(dataKey));
        this.dataKey = dataKey;
    }
    
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length is >= 32 and <= 64);

        // Preserve subclass compatibility, but validate and pin one stable reference.
        var keyData = dataKey;
        if(keyData is { Length: > 64 })
            throw new InvalidOperationException("Blake2b key state must contain at most 64 bytes");
        if(keyData is { Length: > 0 } && !KeyedCapabilityProbe())
            throw new InvalidOperationException(Blake2bNativeCapability.Requirement);

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                fixed (byte* key = keyData)
                {
                    var keyLength = keyData?.Length ?? 0;
                    Multihash.blake2b(input, output, (uint) data.Length, result.Length, key, (uint) keyLength);
                }
            }
        }
    }
}
