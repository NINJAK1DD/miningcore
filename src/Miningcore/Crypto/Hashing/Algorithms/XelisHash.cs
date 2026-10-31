using System.Runtime.InteropServices;
using Miningcore.Contracts;
using Miningcore.Native;

namespace Miningcore.Crypto.Hashing.Algorithms;

[Identifier("xelishash")]
public unsafe class XelisHash : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);
        Contract.Requires<ArgumentException>(data.Length <= 200);

        // v1 operates in place on a 200-byte Keccak state, regardless of input_len.
        // The daemon zero-pads MinerWork; never let native code read past or mutate data.
        Span<ulong> words = stackalloc ulong[25];
        var state = MemoryMarshal.AsBytes(words);
        state.Clear();
        data.CopyTo(state);

        fixed (byte* input = state)
        {
            fixed (byte* output = result)
            {
                Multihash.xelishash(input, output, (uint) state.Length);
            }
        }
    }
}

