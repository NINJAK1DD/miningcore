namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class AuxPowBlockConfirmation
{
    private const string PendingPrefix = "auxpow-block:";

    public static string CreatePending(string blockHash)
    {
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash must not be empty", nameof(blockHash));

        return PendingPrefix + blockHash.Trim();
    }

    public static bool TryGetPendingBlockHash(string value, out string blockHash)
    {
        blockHash = null;

        if(string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(PendingPrefix, StringComparison.Ordinal))
            return false;

        var candidate = value[PendingPrefix.Length..].Trim();
        if(string.IsNullOrEmpty(candidate))
            return false;

        blockHash = candidate;
        return true;
    }
}
