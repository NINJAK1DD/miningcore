using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Wire primitives for Pearl v1.5.5 (d7c517d267f10f5bcbbdc366f3e603942f9a5ab5).
/// These primitives do not validate a mining proof or enable a pool family.
/// </summary>
internal static class PearlConsensus
{
    internal const int IncompleteHeaderSize = 76;
    internal const int HeaderSize = 108;
    internal const int DensePublicDataSize = 164;
    internal const int MaximumProofSize = 60000;
    internal static readonly BigInteger MaximumTarget = (BigInteger.One << 256) - 1;

    internal static uint MainnetCertificateVersion(long height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        return PearlNetworkRules.At(PearlNetwork.Mainnet, height).CertificateVersion;
    }

    internal static BigInteger DecodeTarget(uint bits)
    {
        var exponent = (int) (bits >> 24);
        var mantissa = bits & 0x007fffff;
        if((bits & 0x00800000) != 0 || mantissa == 0)
            throw new ArgumentOutOfRangeException(nameof(bits), "Target must be positive");

        var target = exponent <= 3
            ? new BigInteger(mantissa >> (8 * (3 - exponent)))
            : new BigInteger(mantissa) << (8 * (exponent - 3));
        if(target <= 0 || target > MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(bits), "Target is outside uint256");
        return target;
    }

    /// <summary>Encode a positive target, rounding toward harder work.</summary>
    internal static uint EncodeTarget(BigInteger target)
    {
        if(target <= 0 || target > MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(target));
        var size = target.GetByteCount(isUnsigned: true);
        var compact = (uint) (size <= 3 ? target << (8 * (3 - size)) : target >> (8 * (size - 3)));
        if((compact & 0x00800000) != 0)
        {
            compact >>= 8;
            size++;
        }
        return compact | ((uint) size << 24);
    }

    /// <summary>
    /// Scale only a previously validated configuration. Consensus saturates;
    /// pool work must reject overflowing bounds instead of assigning trivial work.
    /// </summary>
    internal static BigInteger ScaleTarget(BigInteger target, ulong factor, bool saturate)
    {
        if(target <= 0 || target > MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(target));
        if(factor == 0)
            throw new ArgumentOutOfRangeException(nameof(factor));
        var result = target * factor;
        if(result <= MaximumTarget)
            return result;
        if(saturate)
            return MaximumTarget;
        throw new ArgumentOutOfRangeException(nameof(target), "Scaled pool target overflows uint256");
    }

    internal static byte[] SerializeIncompleteHeader(uint version, ReadOnlySpan<byte> previousHashWire,
        ReadOnlySpan<byte> merkleRootWire, uint timestamp, uint bits)
    {
        if(previousHashWire.Length != 32 || merkleRootWire.Length != 32)
            throw new ArgumentException("Wire hashes must contain exactly 32 bytes");
        DecodeTarget(bits);
        var result = new byte[IncompleteHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(result, version);
        previousHashWire.CopyTo(result.AsSpan(4));
        merkleRootWire.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(68), timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(72), bits);
        return result;
    }

    /// <summary>
    /// Assemble dense certificate framing after cryptographic verification by the caller.
    /// The return value is certificate + header, without transaction count/transactions.
    /// </summary>
    internal static byte[] SerializeCertifiedHeader(ReadOnlySpan<byte> incompleteHeader,
        uint certificateVersion, ReadOnlySpan<byte> publicData, ReadOnlySpan<byte> proof)
    {
        if(incompleteHeader.Length != IncompleteHeaderSize)
            throw new ArgumentException("Incomplete header must contain 76 bytes", nameof(incompleteHeader));
        if(certificateVersion is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(certificateVersion));
        if(publicData.Length != DensePublicDataSize)
            throw new ArgumentException("Only dense public data is supported", nameof(publicData));
        if(proof.IsEmpty || proof.Length > MaximumProofSize)
            throw new ArgumentOutOfRangeException(nameof(proof));

        DecodeTarget(BinaryPrimitives.ReadUInt32LittleEndian(incompleteHeader[72..]));
        Span<byte> commitmentInput = stackalloc byte[4 + DensePublicDataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(commitmentInput, certificateVersion);
        publicData.CopyTo(commitmentInput[4..]);
        var header = new byte[HeaderSize];
        incompleteHeader.CopyTo(header);
        DoubleSha256(commitmentInput).CopyTo(header, IncompleteHeaderSize);
        var certificateSize = 4 + 32 + (certificateVersion == 1 ? 0 : 4) + publicData.Length + 4 + proof.Length;
        var result = new byte[certificateSize + HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(result, certificateVersion);
        DoubleSha256(header).CopyTo(result, 4);
        var offset = 36;
        if(certificateVersion != 1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), (uint) publicData.Length);
            offset += 4;
        }
        publicData.CopyTo(result.AsSpan(offset));
        offset += publicData.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), (uint) proof.Length);
        proof.CopyTo(result.AsSpan(offset + 4));
        header.CopyTo(result, certificateSize);
        return result;
    }

    private static byte[] DoubleSha256(ReadOnlySpan<byte> value) => SHA256.HashData(SHA256.HashData(value));
}
