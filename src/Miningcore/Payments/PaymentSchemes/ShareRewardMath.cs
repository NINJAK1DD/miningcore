namespace Miningcore.Payments.PaymentSchemes;

internal static class ShareRewardMath
{
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
            var factor = delta > 94 ? 0m : (decimal) Math.ScaleB(1, -delta);
            foreach(var miner in weights.Keys.ToArray())
                weights[miner] *= factor;
            // Re-sum the rounded per-address weights so allocations cannot
            // exceed the reward due to a differently rounded running total.
            Total = weights.Values.Sum();
            exponent = ratioExponent;
        }
        var relativeExponent = ratioExponent - exponent.Value;
        var weight = relativeExponent < -94 ? 0m : (decimal) Math.ScaleB(mantissa, relativeExponent);
        weights[address] = weights.GetValueOrDefault(address) + weight;
        Total += weight;
    }

    internal IEnumerable<KeyValuePair<string, decimal>> Fractions => Total > 0
        ? weights.Select(x => new KeyValuePair<string, decimal>(x.Key, x.Value / Total))
        : Enumerable.Empty<KeyValuePair<string, decimal>>();
}
