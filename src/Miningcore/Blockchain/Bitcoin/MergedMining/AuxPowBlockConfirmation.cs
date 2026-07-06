namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class AuxPowBlockConfirmation
{
    private const string PendingPrefix = "auxpow-block:";
    private const string UncertainPrefix = "auxpow-uncertain:";

    public static string CreatePending(string blockHash)
    {
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash must not be empty", nameof(blockHash));

        return PendingPrefix + blockHash.Trim();
    }

    public static bool TryGetPendingBlockHash(string value, out string blockHash)
    {
        return TryGetBlockHash(value, PendingPrefix, out blockHash);
    }

    public static string CreateUncertain(string blockHash)
    {
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash must not be empty", nameof(blockHash));

        return UncertainPrefix + blockHash.Trim();
    }

    public static bool TryGetUncertainBlockHash(string value, out string blockHash)
    {
        return TryGetBlockHash(value, UncertainPrefix, out blockHash);
    }

    private static bool TryGetBlockHash(string value, string prefix, out string blockHash)
    {
        blockHash = null;

        if(string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var candidate = value[prefix.Length..].Trim();
        if(string.IsNullOrEmpty(candidate))
            return false;

        blockHash = candidate;
        return true;
    }
}
