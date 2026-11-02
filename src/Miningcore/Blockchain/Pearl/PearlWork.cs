using System.Numerics;
using System.Security.Cryptography;

namespace Miningcore.Blockchain.Pearl;

internal static class PearlWork
{
    // Pearl mainnet's difficulty-one target. Network identity never changes this pool unit.
    internal static readonly BigInteger DifficultyOne = PearlConsensus.DecodeTarget(0x1b00ffff);
    internal const double MinimumDifficulty = 0.000001;
    internal const double MaximumDifficulty = 1e60;
    internal static readonly double HashesPerDifficulty = (double) (BigInteger.One << 256) / (double) DifficultyOne;

    internal static BigInteger Target(double difficulty)
    {
        if(!double.IsFinite(difficulty) || difficulty is < MinimumDifficulty or > MaximumDifficulty)
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        // Divide by the exact IEEE-754 value, rounding toward harder work. No floating
        // point conversion is used on the target or the jackpot acceptance boundary.
        var bits = (ulong) BitConverter.DoubleToInt64Bits(difficulty);
        var mantissa = new BigInteger((bits & ((1UL << 52) - 1)) | (1UL << 52));
        var exponent = (int) ((bits >> 52) & 0x7ff) - 1023 - 52;
        var target = exponent >= 0 ? DifficultyOne / (mantissa << exponent) :
            (DifficultyOne << -exponent) / mantissa;
        if(target <= 0 || target * (1UL << 24) > PearlConsensus.MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(difficulty), "Unsafe Pearl target");
        return target;
    }

    internal static double Difficulty(BigInteger target)
    {
        if(target <= 0 || target > PearlConsensus.MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(target));
        return (double) DifficultyOne / (double) target;
    }

    internal static string Identity(ReadOnlySpan<byte> header, ReadOnlySpan<byte> verifiedPublicData)
    {
        if(header.Length != 76 || verifiedPublicData.Length != 164)
            throw new ArgumentException("Invalid verified Pearl work");
        Span<byte> bytes = stackalloc byte[240];
        header.CopyTo(bytes);
        verifiedPublicData.CopyTo(bytes[76..]);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
