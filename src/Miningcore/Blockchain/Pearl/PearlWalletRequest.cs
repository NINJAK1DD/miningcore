using System.IO;

namespace Miningcore.Blockchain.Pearl;

/// <summary>Oyster v1.5.5 request encoding; this does not send or reconcile a payment.</summary>
internal static class PearlWalletRequest
{
    internal const long GrainsPerPearl = 100_000_000;
    internal const long MaximumMoney = 2_100_000_000L * GrainsPerPearl;

    internal static decimal FeeReserve(int unspentOutputs, int recipients, long rate)
    {
        if(unspentOutputs is < 1 or > 10000 || recipients is < 1 or > 100 || rate is < 1000 or > GrainsPerPearl)
            throw new InvalidDataException("Invalid Pearl fee funding evidence");
        // Budget every wallet input (not just the expected coin selection), plus
        // recipients, marker and change. 300 bytes/input is conservative for Oyster
        // key spends; the 2x multiplier matches reconciliation's fee ceiling.
        var bytes = checked(16 + unspentOutputs * 300 + (recipients + 2) * 43);
        if(bytes > 500000) throw new InvalidDataException("Oyster wallet requires input consolidation before payout");
        return Math.Max(PearlPayoutHandler.MarkerAmount, decimal.Ceiling(bytes * 2m * rate / 1000) / GrainsPerPearl);
    }

    internal static object[] SendMany(IReadOnlyDictionary<string, decimal> amounts, PearlNetwork network,
        long feeRateGrainsPerKb, int minimumConfirmations = 1)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        if(amounts.Count is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(amounts));
        // Deliberate pool policy: no zero-fee payment and at most 1 PRL/kB.
        if(feeRateGrainsPerKb is < 1000 or > GrainsPerPearl)
            throw new ArgumentOutOfRangeException(nameof(feeRateGrainsPerKb));
        if(minimumConfirmations is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(minimumConfirmations));
        var canonical = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        long total = 0;
        foreach(var (address, value) in amounts)
        {
            PearlAddress.RewardScript(address, PearlNetworkRules.AddressPrefix(network));
            if(value <= 0 || value > MaximumMoney / (decimal) GrainsPerPearl)
                throw new InvalidDataException("Invalid Pearl payout amount");
            var grains = value * GrainsPerPearl;
            if(decimal.Truncate(grains) != grains)
                throw new InvalidDataException("Pearl payments must contain whole grains");
            // Oyster's RPC schema decodes float64 and btcutil.NewAmount rounds the
            // product away from zero. Reject values that cannot preserve the ledger grain.
            if(new System.Numerics.BigInteger(Math.Round((double) value * GrainsPerPearl,
                    MidpointRounding.AwayFromZero)) != new System.Numerics.BigInteger(grains))
                throw new InvalidDataException("Oyster cannot represent this payout exactly");
            total = checked(total + (long) grains);
            if(total > MaximumMoney || !canonical.TryAdd(address.ToLowerInvariant(), value))
                throw new InvalidDataException("Duplicate address or excessive Pearl payout total");
        }
        // Required third argument is fee PRL/kB. MinConf is fourth. Oyster rejects comments.
        return new object[] { "default", canonical, feeRateGrainsPerKb / (decimal) GrainsPerPearl,
            minimumConfirmations };
    }
}
