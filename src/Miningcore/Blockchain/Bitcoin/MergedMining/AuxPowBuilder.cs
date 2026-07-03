using System.Buffers.Binary;
using System.Security.Cryptography;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class AuxPowBuilder
{
    private static readonly byte[] MergedMiningHeader = { 0xfa, 0xbe, 0x6d, 0x6d };

    public static byte[] BuildCoinbaseCommitment(string auxiliaryBlockHash)
    {
        if(string.IsNullOrEmpty(auxiliaryBlockHash) || auxiliaryBlockHash.Length != 64)
            throw new ArgumentException("Auxiliary block hash must contain exactly 32 bytes", nameof(auxiliaryBlockHash));

        var hashBytes = auxiliaryBlockHash.HexToByteArray();
        var result = new byte[MergedMiningHeader.Length + hashBytes.Length + sizeof(uint) + sizeof(uint)];
        var offset = 0;

        MergedMiningHeader.CopyTo(result, offset);
        offset += MergedMiningHeader.Length;

        hashBytes.CopyTo(result, offset);
        offset += hashBytes.Length;

        // A single auxiliary chain has a one-leaf chain merkle tree and index zero.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, sizeof(uint)), 1);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, sizeof(uint)), 0);

        return result;
    }

    public static byte[] BuildAuxPow(byte[] parentCoinbase, IReadOnlyCollection<byte[]> parentMerkleBranch,
        byte[] parentHeader)
    {
        ArgumentNullException.ThrowIfNull(parentCoinbase);
        ArgumentNullException.ThrowIfNull(parentMerkleBranch);
        ArgumentNullException.ThrowIfNull(parentHeader);

        if(parentHeader.Length != 80)
            throw new ArgumentException("Parent block header must contain exactly 80 bytes", nameof(parentHeader));

        foreach(var step in parentMerkleBranch)
        {
            if(step is not { Length: 32 })
                throw new ArgumentException("Every parent merkle branch step must contain exactly 32 bytes", nameof(parentMerkleBranch));
        }

        using var stream = new MemoryStream();

        // CMerkleTx::tx
        stream.Write(parentCoinbase);

        // CMerkleTx::hashBlock. Dogecoin does not use this field during AuxPoW
        // validation, but serialising the actual parent SHA256d hash is canonical.
        stream.Write(DoubleSha256(parentHeader));

        // CMerkleTx::vMerkleBranch and nIndex. The parent coinbase is transaction zero.
        WriteCompactSize(stream, (ulong) parentMerkleBranch.Count);
        foreach(var step in parentMerkleBranch)
            stream.Write(step);
        WriteInt32(stream, 0);

        // CAuxPow::vChainMerkleBranch and nChainIndex. A single auxiliary chain
        // requires no chain branch and always occupies index zero.
        WriteCompactSize(stream, 0);
        WriteInt32(stream, 0);

        // CAuxPow::parentBlock (CPureBlockHeader)
        stream.Write(parentHeader);

        return stream.ToArray();
    }

    private static byte[] DoubleSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var first = sha256.ComputeHash(data);
        return sha256.ComputeHash(first);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteCompactSize(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];

        if(value < 253)
        {
            stream.WriteByte((byte) value);
        }
        else if(value <= ushort.MaxValue)
        {
            stream.WriteByte(253);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort) value);
            stream.Write(buffer[..sizeof(ushort)]);
        }
        else if(value <= uint.MaxValue)
        {
            stream.WriteByte(254);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint) value);
            stream.Write(buffer[..sizeof(uint)]);
        }
        else
        {
            stream.WriteByte(255);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            stream.Write(buffer);
        }
    }
}
