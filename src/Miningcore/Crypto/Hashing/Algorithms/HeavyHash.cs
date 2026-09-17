using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("heavyhash")]
public unsafe class HeavyHash : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        // The matrix seed is derived from input bytes 4 through 35.
        Contract.Requires<ArgumentException>(data.Length >= 36);
        Contract.Requires<ArgumentException>(result.Length >= 32);

        fixed (byte* input = data)
        {
            fixed (byte* output = result)
            {
                Multihash.heavyhash(input, output, (uint) data.Length);
            }
        }
    }
}
