using System;
using System.Numerics;

namespace Miningcore.Blockchain.Xelis;

public static class XelisUtils
{
    internal static bool IsSupportedAlgorithm(string algorithm) =>
        algorithm is XelisConstants.AlgorithmXelisHashV1 or XelisConstants.AlgorithmXelisHashV2;

    internal static bool HasCompatibleConsensus(DaemonResponses.GetBlockHeaderResponse header, DaemonResponses.GetBlockTemplateResponse work) =>
        IsSupportedAlgorithm(header.Algorithm) && header.Algorithm == work.Algorithm && header.Difficulty == work.Difficulty;

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
