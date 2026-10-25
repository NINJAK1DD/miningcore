using System;
using System.Numerics;

namespace Miningcore.Blockchain.Xelis;

public static class XelisUtils
{
    public static byte[] DifficultyToTarget(double difficulty)
    {
        // Beyond the consecutive-integer range, RPC integers must use the exact overload.
        if(!double.IsFinite(difficulty) || difficulty < 1 || difficulty > 9007199254740991d ||
            difficulty != Math.Truncate(difficulty))
            throw new ArgumentOutOfRangeException(nameof(difficulty), "Expected an exact positive integer difficulty");

        return DifficultyToTarget(new BigInteger(difficulty));
    }

    public static byte[] DifficultyToTarget(BigInteger difficulty)
    {
        if(difficulty < BigInteger.One || difficulty > XelisConstants.Diff1Target)
            throw new ArgumentOutOfRangeException(nameof(difficulty), "Expected a positive 256-bit difficulty");

        // xelis_common::difficulty: U256::MAX / difficulty, rounded down.
        var target = (XelisConstants.Diff1Target / difficulty).ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[XelisConstants.TargetPaddingLength];
        target.CopyTo(result.AsSpan(result.Length - target.Length));
        return result;
    }

    public static long UnixTimeStamp(DateTime date)
    {
        long unixTimeStamp = date.ToUniversalTime().Ticks - new DateTime(1970, 1, 1, 0, 0, 0).Ticks;
        unixTimeStamp /= TimeSpan.TicksPerMillisecond;
        return unixTimeStamp;
    }

    public static bool CheckDiff(Span<byte> hashBytes, double difficulty)
    {
        byte[] targetBytes = DifficultyToTarget(difficulty);

        return CheckDiff(hashBytes, targetBytes);
    }

    public static bool CheckDiff(Span<byte> hashBytes, byte[] targetBytes)
    {
        ArgumentNullException.ThrowIfNull(targetBytes);
        if(hashBytes.Length != XelisConstants.HashSize || targetBytes.Length != XelisConstants.TargetPaddingLength)
            throw new ArgumentException("Xelis hashes and targets must contain exactly 32 bytes");

        for (int i = 0; i < 32; i++)
        {
            if (hashBytes[i] < targetBytes[i])
                return true;

            if (hashBytes[i] > targetBytes[i])
                return false;
        }

        // The daemon accepts equality too.
        return true;
    }
}
