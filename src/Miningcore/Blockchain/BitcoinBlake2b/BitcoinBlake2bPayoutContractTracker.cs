using System.Collections.Concurrent;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// The payout manager resolves/reconfigures handlers on each cycle. Keep the
// first verified contract in the process scope rather than the handler scope.
// Changing a pool's chain/schedule requires the documented stop/restart.
public sealed class BitcoinBlake2bPayoutContractTracker
{
    private readonly ConcurrentDictionary<string, string> contracts = new(StringComparer.Ordinal);

    internal void Attest(string poolId, string chain, BitcoinBlake2bMaturity schedule)
    {
        var observed = $"{chain}:{schedule.Start}:{schedule.Enforce}:{schedule.Release}:{schedule.Maturity}";
        if(contracts.GetOrAdd(poolId, observed) != observed)
            throw new InvalidOperationException("Bitcoin BLAKE2b payout chain or maturity contract changed; stop and reconcile before restarting");
    }
}
