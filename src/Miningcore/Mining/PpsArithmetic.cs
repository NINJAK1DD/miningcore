using System.Numerics;

namespace Miningcore.Mining;

/// <summary>Version 1: exact binary64 inputs, exact decimal fee, one scale-24 truncation.</summary>
internal static class PpsArithmetic
{
    internal const short LegacyDecimal = 0;
    internal const short CoreDrpBinary64V1 = 1;
    private static readonly BigInteger LiabilityScale = BigInteger.Pow(10, 24);
    private static readonly BigInteger LiabilityLimit = BigInteger.Pow(10, 38);
    private static readonly BigInteger RewardPercentageDivisor = new(10_000_000_000L);

    internal static void ValidateRetainedPercent(decimal retainedPercent)
    {
        if(retainedPercent <= 0 || retainedPercent > 100)
            throw new InvalidDataException("Invalid PPS retained percentage");
        var (coefficient, denominator) = Decimal(retainedPercent);
        while(denominator > 1 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            denominator /= 10;
        }
        if(denominator > LiabilityScale)
            throw new InvalidDataException("PPSLiabilityV1 retained percentage exceeds 24 fractional digits");
    }

    private static (BigInteger N, BigInteger D) Binary64(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int) ((bits >> 52) & 2047);
        // Native hashing may enable denormals-are-zero on this thread. Validate
        // the wire bits without floating-point comparisons or arithmetic.
        if((bits >> 63) != 0 || bits == 0 || exponent == 2047)
            throw new InvalidDataException("PPS difficulty must be positive finite binary64");
        var significand = new BigInteger(bits & ((1UL << 52) - 1));
        if(exponent != 0) significand += BigInteger.One << 52;
        var shift = exponent == 0 ? -1074 : exponent - 1075;
        return shift >= 0 ? (significand << shift, BigInteger.One) :
            (significand, BigInteger.One << -shift);
    }

    private static (BigInteger N, BigInteger D) Decimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var n = new BigInteger((uint) bits[0]) | new BigInteger((uint) bits[1]) << 32 |
            new BigInteger((uint) bits[2]) << 64;
        if(bits[3] < 0) n = -n;
        return (n, BigInteger.Pow(10, (bits[3] >> 16) & 255));
    }

    internal static decimal Calculate(long rewardSatoshis, double assigned,
        double network, decimal retainedPercent)
    {
        if(rewardSatoshis <= 0 || retainedPercent <= 0 || retainedPercent > 100)
            throw new InvalidDataException("Invalid PPS reward or retained percentage");
        ValidateRetainedPercent(retainedPercent);
        var (a, ad) = Binary64(assigned);
        var (n, nd) = Binary64(network);
        var (p, pd) = Decimal(retainedPercent);
        var numerator = rewardSatoshis * a * nd * p;
        var denominator = RewardPercentageDivisor * ad * n * pd;
        var scaled = numerator * LiabilityScale / denominator;
        if(scaled <= 0 || scaled >= LiabilityLimit)
            throw new InvalidDataException("PPSLiabilityV1 is outside positive NUMERIC(38,24)");
        byte scale = 24;
        while(scale > 0 && scaled % 10 == 0) { scaled /= 10; scale--; }
        if(scaled >= BigInteger.One << 96)
            throw new InvalidDataException("Exact PPSLiabilityV1 is not representable by the current decimal transport");
        return new decimal((int) (uint) (scaled & uint.MaxValue),
            (int) (uint) ((scaled >> 32) & uint.MaxValue),
            (int) (uint) ((scaled >> 64) & uint.MaxValue), false, scale);
    }

    internal static bool WithinMaximum(decimal calculated, long rewardSatoshis,
        double assigned, double network)
    {
        if(rewardSatoshis <= 0 || calculated <= 0) return false;
        var (a, ad) = Binary64(assigned);
        var (n, nd) = Binary64(network);
        var (c, cd) = Decimal(calculated);
        return c * 100_000_000 * ad * n <= new BigInteger(rewardSatoshis) * a * nd * cd;
    }
}
