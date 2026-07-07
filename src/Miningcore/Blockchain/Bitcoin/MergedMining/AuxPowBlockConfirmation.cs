namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class AuxPowBlockConfirmation
{
    private const string PendingPrefix = "auxpow-block:";
    private const string ClaimPrefix = "auxpow-claim:";
    private const string ParentUncertainPrefix = "parent-uncertain:";

    public static string CreatePending(string blockHash, int coinbaseMisses = 0)
    {
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash must not be empty", nameof(blockHash));
        if(coinbaseMisses < 0)
            throw new ArgumentOutOfRangeException(nameof(coinbaseMisses));

        var trimmedBlockHash = blockHash.Trim();
        return coinbaseMisses == 0
            ? PendingPrefix + trimmedBlockHash
            : $"{PendingPrefix}{trimmedBlockHash}:{coinbaseMisses}";
    }

    public static bool TryGetPendingBlockHash(string value, out string blockHash)
    {
        return TryGetPendingBlockHash(value, out blockHash, out _);
    }

    public static bool TryGetPendingBlockHash(string value, out string blockHash,
        out int coinbaseMisses)
    {
        blockHash = null;
        coinbaseMisses = 0;

        if(string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(PendingPrefix, StringComparison.Ordinal))
            return false;

        var parts = value[PendingPrefix.Length..].Split(':');
        if(parts.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(parts[0]))
            return false;

        if(parts.Length == 2 &&
            (!int.TryParse(parts[1], out coinbaseMisses) || coinbaseMisses < 0))
            return false;

        blockHash = parts[0].Trim();
        return true;
    }

    public static string CreateClaim(string blockHash, string parentBlock, int definitiveMisses = 0)
    {
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash must not be empty", nameof(blockHash));
        if(string.IsNullOrWhiteSpace(parentBlock))
            throw new ArgumentException("Parent block header must not be empty", nameof(parentBlock));

        return $"{ClaimPrefix}{blockHash.Trim()}:{parentBlock.Trim()}:{definitiveMisses}";
    }

    public static bool TryGetClaim(string value, out string blockHash, out string parentBlock,
        out int definitiveMisses)
    {
        blockHash = null;
        parentBlock = null;
        definitiveMisses = 0;

        if(string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(ClaimPrefix, StringComparison.Ordinal))
            return false;

        var parts = value[ClaimPrefix.Length..].Split(':');
        if(parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) || !int.TryParse(parts[2], out definitiveMisses) ||
            definitiveMisses < 0)
            return false;

        blockHash = parts[0];
        parentBlock = parts[1];
        return true;
    }

    public static string CreateParentUncertain(string blockHash, int definitiveMisses = 0)
    {
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash must not be empty", nameof(blockHash));

        return $"{ParentUncertainPrefix}{blockHash.Trim()}:{definitiveMisses}";
    }

    public static bool TryGetParentUncertain(string value, out string blockHash,
        out int definitiveMisses)
    {
        blockHash = null;
        definitiveMisses = 0;

        if(string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(ParentUncertainPrefix, StringComparison.Ordinal))
            return false;

        var parts = value[ParentUncertainPrefix.Length..].Split(':');
        if(parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) ||
            !int.TryParse(parts[1], out definitiveMisses) || definitiveMisses < 0)
            return false;

        blockHash = parts[0];
        return true;
    }

}
