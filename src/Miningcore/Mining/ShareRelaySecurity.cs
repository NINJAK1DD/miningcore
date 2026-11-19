using Miningcore.Configuration;

namespace Miningcore.Mining;

internal static class ShareRelaySecurity
{
    private static bool IsPlaceholder(string key) =>
        key?.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase) == true ||
        key?.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase) == true;

    internal static void Validate(ClusterConfig config, Action<string> warn = null)
    {
        // Keep configured PPS routes protected even while disabled.
        if(config?.Pools?.Any(pool => pool.Enabled &&
            pool.PaymentProcessing?.PayoutScheme == PayoutScheme.PPS) != true)
        {
            if(warn != null && (IsPlaceholder(config?.ShareRelay?.SharedEncryptionKey?.Trim()) ||
                config?.ShareRelays?.Any(endpoint => IsPlaceholder(endpoint?.SharedEncryptionKey?.Trim())) == true))
                warn("Share relay sharedEncryptionKey looks like an example placeholder; replace it on both ends with a randomly generated secret");
            return;
        }
        static bool HasKey(string key)
        {
            var value = key?.Trim();
            return value is { Length: >= 32 } && !IsPlaceholder(value);
        }
        if(config.ShareRelay is { } sender && (sender.Connect || !HasKey(sender.SharedEncryptionKey)))
            throw new PoolStartupException("PPS relay senders require bind mode and a sharedEncryptionKey of at least 32 characters; use a randomly generated secret, not an example placeholder");
        if(config.ShareRelays?.Any(endpoint => endpoint == null || !HasKey(endpoint.SharedEncryptionKey)) == true)
            throw new PoolStartupException("Every PPS receiver endpoint requires a sharedEncryptionKey of at least 32 characters; use a randomly generated secret, not an example placeholder");
    }
}
