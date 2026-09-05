using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Util;

namespace Miningcore.Blockchain.BitcoinBlake2b;

/// <summary>
/// Source-verified Bitcoin Knots header-v2 and Sia-style work primitives.
/// The byte layout mirrors Bitcoin Knots v29.4.1.knots20260508 commit
/// 8c85b1585dac23f964e2dd32045624de7f02aa58.
/// </summary>
internal static class BitcoinBlake2bHeader
{
    internal const uint HeaderV2Flag = 0x80000000;
    internal const int HeaderSize = 164;
    internal const int CommitmentSize = 32;
    internal const int Coinbase1Size = 39;
    internal const int ConnectionExtraNonceSize = 4;
    internal const int MinerExtraNonceSize = 8;
    internal const int HeaderExtraNonceSize = 16;
    internal const int MinerNonceSize = 8;
    internal const int MinerTimeSize = 8;
    internal const byte UseTimeOffsetFlag = 4;
    internal const byte ReservedHighFlags = 0xc0;

    private static readonly Blake2b Blake2b256 = new();
    private static readonly byte[] Zero16 = new byte[16];

    // PreviousBlockHash and MerkleRoot are RPC display-order bytes. XorKey
    // and MergeMiningRightHandSide are serialized-order bytes, as is the
    // header extranonce passed separately to serialization/hashing methods.
    internal sealed record ConsensusFields(
        uint Version,
        byte[] PreviousBlockHash,
        byte[] MerkleRoot,
        uint TimeOnWire,
        uint Bits,
        ushort TransactionCount,
        byte Flags,
        byte XorKeyMaskClearBits,
        byte[] XorKey,
        uint Height,
        byte[] MergeMiningRightHandSide);

    internal static byte[] HeaderCommitment(ConsensusFields fields)
    {
        Validate(fields);

        var h1 = new byte[119];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(h1.AsSpan(offset, 4),
            fields.Version | HeaderV2Flag);
        offset += 4;

        // GBT displays uint256 values in big-endian order. H1 commits the
        // previous hash in that sane display order, but the merkle root in
        // ordinary uint256 serialization order.
        fields.PreviousBlockHash.CopyTo(h1, offset);
        offset += 32;
        BinaryPrimitives.WriteUInt32LittleEndian(h1.AsSpan(offset, 4),
            fields.Height);
        offset += 4;
        CopyReversed(fields.MerkleRoot, h1.AsSpan(offset, 32));
        offset += 32;
        BinaryPrimitives.WriteUInt32LittleEndian(h1.AsSpan(offset, 4),
            fields.TimeOnWire);
        offset += 4;
        h1[offset++] = 0; // reserved extended-time byte
        BinaryPrimitives.WriteUInt32LittleEndian(h1.AsSpan(offset, 4),
            fields.Bits);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(h1.AsSpan(offset, 4),
            fields.TransactionCount);
        offset += 4;
        h1[offset++] = fields.Flags;
        h1[offset++] = fields.XorKeyMaskClearBits;
        TaggedSha256("Bitcoin block hash PoW XOR key", fields.XorKey)
            .CopyTo(h1, offset);

        var h2 = new byte[96];
        TaggedSha256("Bitcoin block header 1", h1).CopyTo(h2, 0);
        // h2[32..64] is the merge-mining left-hand side and remains zero.
        fields.MergeMiningRightHandSide.CopyTo(h2, 64);
        return TaggedSha256("Merge-mining hook", h2);
    }

    internal static byte[] Coinbase1(ReadOnlySpan<byte> commitment)
    {
        if(commitment.Length != CommitmentSize)
            throw new ArgumentException("Header commitment must be 32 bytes",
                nameof(commitment));

        var result = new byte[Coinbase1Size];
        commitment.CopyTo(result.AsSpan(3, 32));
        return result;
    }

    internal static byte[] WorkRoot(ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> headerExtraNonce)
    {
        if(commitment.Length != CommitmentSize)
            throw new ArgumentException("Header commitment must be 32 bytes",
                nameof(commitment));
        if(headerExtraNonce.Length != HeaderExtraNonceSize)
            throw new ArgumentException("Header extranonce must be 16 bytes",
                nameof(headerExtraNonce));

        var input = new byte[52];
        // uint32 zero, H2, then the 16-byte header extranonce.
        commitment.CopyTo(input.AsSpan(4, 32));
        headerExtraNonce.CopyTo(input.AsSpan(36, 16));
        return Blake2b(input);
    }

    internal static byte[] HiddenPreviousBlockHash(
        ReadOnlySpan<byte> previousBlockHash)
    {
        if(previousBlockHash.Length != 32)
            throw new ArgumentException("Previous block hash must be 32 bytes",
                nameof(previousBlockHash));

        var result = TaggedSha256("Bitcoin prevblock header, hashed",
            previousBlockHash);
        result.AsSpan(0, 6).Clear();
        return result;
    }

    internal static byte[] BuildAsicInput(ConsensusFields fields,
        ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> minerTime,
        ReadOnlySpan<byte> workRoot)
    {
        Validate(fields);
        if(nonce.Length != MinerNonceSize)
            throw new ArgumentException("Miner nonce must be 8 bytes",
                nameof(nonce));
        if(minerTime.Length != MinerTimeSize)
            throw new ArgumentException("Miner time must be 8 bytes",
                nameof(minerTime));
        if(workRoot.Length != 32)
            throw new ArgumentException("Work root must be 32 bytes",
                nameof(workRoot));

        var h2 = HeaderCommitment(fields);
        var profile = fields.Flags & 3;
        byte[] result;
        var offset = 0;

        switch(profile)
        {
            case 0:
                result = new byte[80];
                HiddenPreviousBlockHash(fields.PreviousBlockHash)
                    .CopyTo(result, offset);
                offset += 32;
                nonce.CopyTo(result.AsSpan(offset, 8));
                offset += 8;
                minerTime.CopyTo(result.AsSpan(offset, 8));
                offset += 8;
                workRoot.CopyTo(result.AsSpan(offset, 32));
                break;

            case 1:
                result = new byte[80];
                nonce.CopyTo(result.AsSpan(offset, 8));
                offset += 8;
                // Knots profile 1 orders nonce3 before time_offset.
                minerTime[4..8].CopyTo(result.AsSpan(offset, 4));
                offset += 4;
                minerTime[..4].CopyTo(result.AsSpan(offset, 4));
                offset += 4;
                workRoot.CopyTo(result.AsSpan(offset, 32));
                offset += 32;
                h2.CopyTo(result, offset);
                break;

            case 2:
            case 3:
                result = new byte[profile == 2 ? 128 : 160];
                offset = profile == 2 ? 48 : 80;
                h2.CopyTo(result, offset);
                offset += 32;
                nonce.CopyTo(result.AsSpan(offset, 8));
                offset += 8;
                minerTime[..4].CopyTo(result.AsSpan(offset, 4));
                offset += 4;
                minerTime[4..8].CopyTo(result.AsSpan(offset, 4));
                offset += 4;
                workRoot.CopyTo(result.AsSpan(offset, 32));
                break;

            default:
                throw new System.Diagnostics.UnreachableException("Two-bit profile selector");
        }

        return result;
    }

    internal static byte[] ComputeHash(ConsensusFields fields,
        ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> minerTime,
        ReadOnlySpan<byte> headerExtraNonce)
    {
        var root = WorkRoot(HeaderCommitment(fields), headerExtraNonce);
        var raw = Blake2b(BuildAsicInput(fields, nonce, minerTime, root));
        var mask = XorMask(fields.XorKey, fields.XorKeyMaskClearBits);
        for(var i = 0; i < raw.Length; i++)
            raw[i] ^= mask[i];

        // This is display/hash comparison order. Header serialization uses
        // little-endian uint256 fields independently.
        return raw;
    }

    internal static byte[] ComputeUnmaskedProfile0Hash(ConsensusFields fields, ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> hiddenPrevious, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> minerTime, ReadOnlySpan<byte> headerExtraNonce)
    {
        if(fields.Flags != 0 || fields.XorKeyMaskClearBits != 0 ||
           fields.XorKey.Length != 16 || fields.XorKey.Any(x => x != 0))
            throw new InvalidOperationException("Cached profile-0 hashing requires fixed time and an unmasked zero-key policy");
        if(hiddenPrevious.Length != 32 || nonce.Length != 8 || minerTime.Length != 8)
            throw new ArgumentException("Invalid profile-0 work dimensions");
        var root = WorkRoot(commitment, headerExtraNonce);
        Span<byte> input = stackalloc byte[80];
        hiddenPrevious.CopyTo(input);
        nonce.CopyTo(input[32..40]);
        minerTime.CopyTo(input[40..48]);
        root.CopyTo(input[48..]);
        return Blake2b(input);
    }

    internal static byte[] Serialize(ConsensusFields fields,
        ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> minerTime,
        ReadOnlySpan<byte> headerExtraNonce)
    {
        Validate(fields);
        if(nonce.Length != MinerNonceSize)
            throw new ArgumentException("Miner nonce must be 8 bytes",
                nameof(nonce));
        if(minerTime.Length != MinerTimeSize)
            throw new ArgumentException("Miner time must be 8 bytes",
                nameof(minerTime));
        if(headerExtraNonce.Length != HeaderExtraNonceSize)
            throw new ArgumentException("Header extranonce must be 16 bytes",
                nameof(headerExtraNonce));

        var result = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4),
            fields.Version | HeaderV2Flag);
        CopyReversed(fields.PreviousBlockHash, result.AsSpan(4, 32));
        CopyReversed(fields.MerkleRoot, result.AsSpan(36, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(68, 4),
            fields.TimeOnWire);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(72, 4),
            fields.Bits);
        nonce.CopyTo(result.AsSpan(76, 8));
        minerTime[4..8].CopyTo(result.AsSpan(84, 4));
        headerExtraNonce.CopyTo(result.AsSpan(88, 16));
        minerTime[..4].CopyTo(result.AsSpan(104, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(108, 2),
            fields.TransactionCount);
        result[110] = fields.Flags;
        result[111] = fields.XorKeyMaskClearBits;
        fields.XorKey.CopyTo(result, 112);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(128, 4),
            fields.Height);
        fields.MergeMiningRightHandSide.CopyTo(result, 132);
        return result;
    }

    internal static byte[] ParseExactHex(string value, int bytes,
        string field)
    {
        if(value?.Length != bytes * 2)
            throw new InvalidDataException(
                $"{field} must contain exactly {bytes * 2} hexadecimal characters");

        try
        {
            return Convert.FromHexString(value);
        }
        catch(FormatException ex)
        {
            throw new InvalidDataException(
                $"{field} must contain only hexadecimal characters", ex);
        }
    }

    internal static BigInteger HashValue(ReadOnlySpan<byte> displayHash) =>
        new(displayHash, isUnsigned: true, isBigEndian: true);

    internal static BigInteger ParseDisplayTarget(string target)
    {
        if(target?.Length != 64 || !BigInteger.TryParse("0" + target,
               NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
               out var result) || result <= 0)
            throw new InvalidDataException(
                "Block template target must be a positive 32-byte hexadecimal integer");

        return result;
    }

    internal static uint ParseCompactBits(string bits)
    {
        if(bits?.Length != 8 || !uint.TryParse(bits,
               NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
               out var result))
            throw new InvalidDataException(
                "Block template bits must be an eight-digit hexadecimal integer");

        return result;
    }

    internal static BigInteger DecodeCompactTarget(uint compact)
    {
        var size = compact >> 24;
        var word = compact & 0x007fffff;
        if(word == 0 || (compact & 0x00800000) != 0 || size > 34)
            throw new InvalidDataException("Compact target is negative, zero or overflowing");

        var value = size <= 3
            ? new BigInteger(word >> checked((int) (8 * (3 - size))))
            : new BigInteger(word) << checked((int) (8 * (size - 3)));
        if(value <= 0 || value >= (BigInteger.One << 256))
            throw new InvalidDataException("Compact target is outside uint256 range");
        return value;
    }

    internal static uint EncodeCompactTarget(BigInteger target)
    {
        if(target <= 0 || target >= (BigInteger.One << 256))
            throw new ArgumentOutOfRangeException(nameof(target));

        var bytes = target.ToByteArray(isUnsigned: true, isBigEndian: true);
        uint mantissa;
        if(bytes.Length <= 3)
        {
            mantissa = 0;
            foreach(var value in bytes)
                mantissa = (mantissa << 8) | value;
            mantissa <<= 8 * (3 - bytes.Length);
        }
        else
            mantissa = (uint) ((bytes[0] << 16) | (bytes[1] << 8) |
                bytes[2]);

        var size = bytes.Length;
        if((mantissa & 0x00800000) != 0)
        {
            mantissa >>= 8;
            size++;
        }

        return ((uint) size << 24) | (mantissa & 0x007fffff);
    }

    internal static BigInteger TargetForDifficulty(double difficulty)
    {
        if(difficulty <= 0 || double.IsNaN(difficulty) ||
           double.IsInfinity(difficulty))
            throw new ArgumentOutOfRangeException(nameof(difficulty));

        // Convert the positive IEEE-754 input to its exact integer ratio.
        // Do not route this through BigRational(double): that legacy helper
        // raises the significand to the exponent and is unsuitable for a
        // financial/consensus target boundary.
        var bits = BitConverter.DoubleToInt64Bits(difficulty);
        var exponentBits = (int) ((bits >> 52) & 0x7ff);
        var fraction = (ulong) bits & 0x000f_ffff_ffff_ffffUL;
        var significand = exponentBits == 0
            ? new BigInteger(fraction)
            : new BigInteger(fraction | 0x0010_0000_0000_0000UL);
        var binaryExponent = exponentBits == 0
            ? -1074
            : exponentBits - 1023 - 52;
        var numerator = binaryExponent >= 0
            ? significand << binaryExponent
            : significand;
        var denominator = binaryExponent < 0
            ? BigInteger.One << -binaryExponent
            : BigInteger.One;
        var target = BitcoinConstants.Diff1 * denominator / numerator;
        if(target <= 0)
            throw new ArgumentOutOfRangeException(nameof(difficulty),
                "Difficulty produces an unrepresentable share target");

        // Compact targets truncate. Decode the value that miners receive and
        // that Miningcore will enforce, never an easier pre-truncation value.
        return DecodeCompactTarget(EncodeCompactTarget(target));
    }

    internal static uint ActivationTarget(uint parentBits, byte shift, uint powLimitBits)
    {
        var parent = DecodeCompactTarget(parentBits);
        var limit = DecodeCompactTarget(powLimitBits);
        var shifted = parent > (limit >> shift) ? limit : parent << shift;
        return EncodeCompactTarget(shifted);
    }

    internal static (bool Accepted, bool Candidate) ClassifyProof(BigInteger hash,
        BigInteger assignedTarget, BigInteger networkTarget)
    {
        if(hash < 0 || hash >= (BigInteger.One << 256))
            throw new ArgumentOutOfRangeException(nameof(hash));
        if(assignedTarget <= 0 || assignedTarget >= (BigInteger.One << 256))
            throw new ArgumentOutOfRangeException(nameof(assignedTarget));
        if(networkTarget <= 0 || networkTarget >= (BigInteger.One << 256))
            throw new ArgumentOutOfRangeException(nameof(networkTarget));
        var candidate = hash <= networkTarget;
        return (candidate || hash <= assignedTarget, candidate);
    }

    internal static double DifficultyForHash(BigInteger hash)
    {
        if(hash <= 0)
            return double.PositiveInfinity;
        return (double) new BigRational(BitcoinConstants.Diff1, hash);
    }

    private static byte[] XorMask(ReadOnlySpan<byte> key, byte clearBits)
    {
        if(key.Length != 16)
            throw new ArgumentException("XOR key must be 16 bytes", nameof(key));
        if(key.SequenceEqual(Zero16))
            return new byte[32];

        var mask = TaggedSha256("Bitcoin block hash PoW XOR mask", key);
        var clearBytes = clearBits / 8;
        mask.AsSpan(0, clearBytes).Clear();
        if(clearBytes < mask.Length)
            mask[clearBytes] &= (byte) (0xff >> (clearBits % 8));
        return mask;
    }

    private static byte[] TaggedSha256(string tag, ReadOnlySpan<byte> data)
    {
        var tagHash = SHA256.HashData(Encoding.ASCII.GetBytes(tag));
        var input = new byte[tagHash.Length * 2 + data.Length];
        tagHash.CopyTo(input, 0);
        tagHash.CopyTo(input, tagHash.Length);
        data.CopyTo(input.AsSpan(tagHash.Length * 2));
        return SHA256.HashData(input);
    }

    private static void CopyReversed(ReadOnlySpan<byte> source,
        Span<byte> destination)
    {
        if(source.Length != destination.Length)
            throw new ArgumentException("Source and destination lengths differ");
        for(var i = 0; i < source.Length; i++)
            destination[i] = source[source.Length - 1 - i];
    }

    private static byte[] Blake2b(ReadOnlySpan<byte> input)
    {
        var result = new byte[32];
        Blake2b256.Digest(input, result);
        return result;
    }

    private static void Validate(ConsensusFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if(fields.PreviousBlockHash?.Length != 32)
            throw new InvalidDataException("Previous block hash must be 32 bytes");
        if(fields.MerkleRoot?.Length != 32)
            throw new InvalidDataException("Merkle root must be 32 bytes");
        if(fields.XorKey?.Length != 16)
            throw new InvalidDataException("XOR key must be 16 bytes");
        if(fields.MergeMiningRightHandSide?.Length != 32)
            throw new InvalidDataException(
                "Merge-mining right-hand side must be 32 bytes");
        if((fields.Flags & ReservedHighFlags) != 0)
            throw new InvalidDataException(
                "BLAKE2b header flags use reserved high bits");
        if(fields.TransactionCount == 0)
            throw new InvalidDataException(
                "BLAKE2b header transaction count must be positive");
    }
}
