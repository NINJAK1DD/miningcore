using Miningcore.Configuration;

namespace Miningcore.Mining;

internal static class ShareRelaySecurity
{
    internal static void Validate(ClusterConfig config)
    {
        // Keep configured PPS routes protected even while disabled.
        if(config?.Pools?.Any(pool => pool.Enabled &&
            pool.PaymentProcessing?.PayoutScheme == PayoutScheme.PPS) != true)
            return;
        static bool HasKey(string key) => key?.Trim().Length >= 32;
        if(config.ShareRelay is { } sender && (sender.Connect || !HasKey(sender.SharedEncryptionKey)))
            throw new PoolStartupException("PPS relay senders require bind mode and a sharedEncryptionKey of at least 32 characters; use a randomly generated secret");
        if(config.ShareRelays?.Any(endpoint => endpoint == null || !HasKey(endpoint.SharedEncryptionKey)) == true)
            throw new PoolStartupException("Every PPS receiver endpoint requires a sharedEncryptionKey of at least 32 characters; unauthenticated accounting transport is not permitted");
    }
}
