using System.Globalization;
using Miningcore.Persistence.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Payments.PaymentSchemes;

internal static class ShareRewardMath
{
    internal static void ValidateShare(Share share, double adjustedDifficulty, ulong settlementHeight, ILogger logger)
    {
        if(double.IsFinite(share.Difficulty) && share.Difficulty > 0 &&
            double.IsFinite(adjustedDifficulty) && adjustedDifficulty > 0 &&
            double.IsFinite(share.NetworkDifficulty) && share.NetworkDifficulty > 0)
            return;

        // Share identifiers are operator-owned financial diagnostics, not RPC
        // error text. JSON escapes controls; never attach arbitrary exceptions,
        // IP addresses, agents or passwords to the log event.
        var identity = new JObject
        {
            ["poolId"] = share.PoolId,
            ["miner"] = share.Miner,
            ["created"] = share.Created.ToString("O", CultureInfo.InvariantCulture),
            ["shareBlockHeight"] = share.BlockHeight,
            ["settlementBlockHeight"] = settlementHeight,
            ["storedDifficulty"] = share.Difficulty,
            ["adjustedDifficulty"] = adjustedDifficulty,
            ["networkDifficulty"] = share.NetworkDifficulty,
        }.ToString(Formatting.None);
        logger.Error("Invalid settlement share " + identity);
        throw new InvalidDataException("Settlement requires finite positive stored, adjusted and network difficulty; share=" + identity);
    }

    internal static void Validate(double difficulty, double network)
    {
        if(!double.IsFinite(difficulty) || difficulty <= 0 || !double.IsFinite(network) || network <= 0)
            throw new InvalidDataException("Settlement requires finite positive share and network difficulty");
    }

    internal static decimal WindowScore(double difficulty, double network, decimal remaining)
    {
        Validate(difficulty, network);
        // A finite input pair may overflow the quotient. Bound it before the
        // decimal conversion; a window never needs more than its remainder.
        var ratio = difficulty / network;
        return ratio >= (double) remaining ? remaining : (decimal) ratio;
    }
}

// PROP has no finite work window. Capping individual ratios would change reward
// proportions, so keep every weight in a common binary scale instead. Only the
// final dimensionless fractions enter reward arithmetic. Weights below decimal
// precision relative to the largest weight round to zero, as with decimal scores.
internal sealed class ProportionalRewardWeights
{
    // Decimal's smallest positive magnitude is 1e-28, between 2^-93 and
    // 2^-94. Beyond this relative exponent, scaled weights cannot retain
    // meaningful decimal precision. Conversion near the boundary also rounds.
    private const int MaximumRelativeExponent = 94;
    private readonly Dictionary<string, decimal> weights = new();
    private int? exponent;
    internal decimal Total { get; private set; }

    internal void Add(string address, double difficulty, double network)
    {
        ShareRewardMath.Validate(difficulty, network);
        var d = Math.ILogB(difficulty);
        var n = Math.ILogB(network);
        var ratioExponent = d - n;
        var mantissa = Math.ScaleB(difficulty, -d) / Math.ScaleB(network, -n);
        if(!exponent.HasValue)
            exponent = ratioExponent;
        else if(ratioExponent > exponent.Value)
        {
            var delta = ratioExponent - exponent.Value;
            var factor = delta > MaximumRelativeExponent ? 0m : (decimal) Math.ScaleB(1, -delta);
            foreach(var miner in weights.Keys.ToArray())
                weights[miner] *= factor;
            // Re-sum the rounded per-address weights so allocations cannot
            // exceed the reward due to a differently rounded running total.
            Total = weights.Values.Sum();
            exponent = ratioExponent;
        }
        var relativeExponent = ratioExponent - exponent.Value;
        var weight = relativeExponent < -MaximumRelativeExponent ? 0m : (decimal) Math.ScaleB(mantissa, relativeExponent);
        weights[address] = weights.GetValueOrDefault(address) + weight;
        Total += weight;
    }

    internal IEnumerable<KeyValuePair<string, decimal>> Fractions => Total > 0
        ? weights.Select(x => new KeyValuePair<string, decimal>(x.Key, x.Value / Total))
        : Enumerable.Empty<KeyValuePair<string, decimal>>();
}
