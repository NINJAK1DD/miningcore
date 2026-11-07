using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Bitcoin;

internal static class BitcoinConfirmationPolicy
{
    public static bool UsesSharedClassification(CoinTemplate template) =>
        template?.Family is CoinFamily.Bitcoin or CoinFamily.Nexa or CoinFamily.Progpow or
            CoinFamily.Satoshicash or CoinFamily.Equihash;

    public static int Resolve(PoolConfig pool)
    {
        var extra = pool.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        var minimum = extra?.MinimumConfirmations ??
            (pool.Template as BitcoinTemplate)?.CoinbaseMinConfimations ??
            BitcoinConstants.CoinbaseMinConfimations;
        if(minimum <= 0)
            throw new PoolStartupException(
                $"minimumConfirmations must be positive (got {minimum})", pool.Id);
        return minimum;
    }
}
