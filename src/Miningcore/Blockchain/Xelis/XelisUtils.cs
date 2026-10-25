using System;
using System.Numerics;

namespace Miningcore.Blockchain.Xelis;

public static class XelisUtils
{
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

}
