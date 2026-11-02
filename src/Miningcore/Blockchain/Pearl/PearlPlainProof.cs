using System.Buffers.Binary;
using System.IO;
using Miningcore.Stratum;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Allocation-free preflight of the pinned upstream fixed-integer bincode dense format.
/// This validates structure and resource bounds, never cryptographic validity.
/// </summary>
internal static class PearlPlainProof
{
    internal const int MaximumBytes = 8 * 1024 * 1024;

    internal readonly record struct Dimensions(uint M, uint N, uint K, ushort Rank,
        int Rows, int Columns, int CanonicalLength)
    {
        internal ulong WorkFactor => checked((ulong) Rows * (ulong) Columns * (K - K % Rank));
        internal ulong RankAdjustedWorkFactor =>
            checked((ulong) Rows * (ulong) Columns * ((K - K % Rank) / Rank) * 128);
    }

    // Admission policy, not a consensus rule. Larger legal dense profiles can
    // verify cheaply but exceed the bounded prover and permanently occupy its
    // durable candidate queue. Admit only the certificate profile tested under
    // the shipped worker's fixed memory/CPU limits.
    internal static void RequirePoolProfile(Dimensions dimensions)
    {
        if(dimensions.M > 32772 || dimensions.N > 15360 || dimensions.K != 32768 ||
            dimensions.Rank != 128 || dimensions.Rows != 8 || dimensions.Columns != 16)
            throw new StratumException(StratumError.Other,
                "Unsupported Pearl pool profile; use m<=32772, n<=15360, k=32768, rank=128, tile=8x16");
    }

    internal static Dimensions Inspect(ReadOnlySpan<byte> data)
    {
        if(data.Length is < 32 or > MaximumBytes)
            throw new InvalidDataException("Invalid Pearl proof length");

        var reader = new Reader(data);
        var m = reader.BoundedInteger(1, 1 << 24);
        var n = reader.BoundedInteger(1, 1 << 24);
        var k = reader.BoundedInteger(1024, 1 << 16);
        var rank = reader.BoundedInteger(32, 1024);
        if((rank & (rank - 1)) != 0 || k % 64 != 0 || k < 16 * rank || k > 4 * rank * rank)
            throw new InvalidDataException("Invalid Pearl matrix dimensions");

        var rows = reader.Matrix(m, k);
        var columns = reader.Matrix(n, k);
        var inputSize = (ulong) (rows + columns) * (k - k % rank);
        if(rows * columns is < 32 or > 256 || inputSize > 1 << 22)
            throw new InvalidDataException("Invalid Pearl sampled matrix dimensions");

        var canonicalLength = reader.Position;
        // Legacy dense encoding omits the Option tag. Current dense encoding ends in None.
        // Neither an MoE variant nor trailing input may reach the dense verifier.
        if(reader.Remaining == 1 && reader.ReadByte() != 0 || reader.Remaining != 0)
            throw new InvalidDataException("Unsupported or trailing Pearl proof data");

        return new Dimensions(m, n, k, (ushort) rank, rows, columns, canonicalLength);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        internal int Position { get; private set; }
        internal int Remaining => data.Length - Position;

        private ReadOnlySpan<byte> Take(int size)
        {
            if(size < 0 || size > Remaining)
                throw new InvalidDataException("Truncated Pearl proof");
            var result = data.Slice(Position, size);
            Position += size;
            return result;
        }

        internal byte ReadByte() => Take(1)[0];
        private ulong Integer() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        internal uint BoundedInteger(uint min, uint max)
        {
            var value = Integer();
            if(value < min || value > max)
                throw new InvalidDataException("Pearl proof count exceeds bounds");
            return (uint) value;
        }

        internal int Matrix(uint dimension, uint k)
        {
            var totalExpected = ((ulong) dimension * k + 1023) / 1024;
            var leaves = BoundedInteger(1, (uint) (MaximumBytes / 1032));
            // Each nested leaf is a length-prefixed Vec<u8>, not a raw [u8; 1024].
            if(leaves > (uint) (Remaining / 1032))
                throw new InvalidDataException("Truncated Pearl leaves");
            for(var i = 0; i < leaves; i++)
            {
                BoundedInteger(1024, 1024);
                Take(1024);
            }

            BoundedInteger(leaves, leaves);
            SortedIndices(leaves, totalExpected);
            if(Integer() != totalExpected)
                throw new InvalidDataException("Pearl Merkle tree size does not match dimensions");
            Take(32); // Committed root; the cryptographic verifier must check it.
            var siblings = BoundedInteger(0, (uint) Math.Min((ulong) leaves * 30, MaximumBytes / 32));
            Take(checked((int) siblings * 32));
            var rowCount = BoundedInteger(1, 128);
            SortedIndices(rowCount, dimension);
            return (int) rowCount;
        }

        private void SortedIndices(uint count, ulong exclusiveLimit)
        {
            ulong previous = 0;
            if(count > Remaining / 8)
                throw new InvalidDataException("Truncated Pearl indices");
            for(var i = 0; i < count; i++)
            {
                var index = Integer();
                if(index >= exclusiveLimit || i > 0 && index <= previous)
                    throw new InvalidDataException("Invalid Pearl index ordering or range");
                previous = index;
            }
        }
    }
}
