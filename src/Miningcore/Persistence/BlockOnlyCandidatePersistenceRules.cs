using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Persistence;

internal sealed record BlockOnlyCandidatePersistenceRule(string Type,
    string StableIdentity, string ConflictClause);

internal static class BlockOnlyCandidatePersistenceRules
{
    private static readonly IReadOnlyDictionary<string,
        BlockOnlyCandidatePersistenceRule> Rules =
        new Dictionary<string, BlockOnlyCandidatePersistenceRule>(
            StringComparer.Ordinal)
        {
            ["auxpow"] = new("auxpow", "poolId + blockHash",
                " ON CONFLICT (poolid, hash) WHERE type = 'auxpow' DO NOTHING"),
            ["auxpow-claim"] = new("auxpow-claim",
                "poolId + blockHash + claim identity without attempt suffix",
                " ON CONFLICT (poolid, hash, (regexp_replace(transactionconfirmationdata, ':[0-9]+$', ''))) WHERE type = 'auxpow-claim' DO NOTHING"),
            ["merged-parent"] = new("merged-parent", "poolId + blockHash",
                " ON CONFLICT (poolid, hash) WHERE type IN ('merged-parent', 'merged-parent-uncertain') DO NOTHING"),
            ["merged-parent-uncertain"] = new("merged-parent-uncertain",
                "poolId + blockHash",
                " ON CONFLICT (poolid, hash) WHERE type IN ('merged-parent', 'merged-parent-uncertain') DO NOTHING"),
        };

    internal static IReadOnlyCollection<string> DeclaredTypes =>
        Rules.Keys.ToArray();

    internal static bool TryGet(string type,
        out BlockOnlyCandidatePersistenceRule rule)
    {
        if(type != null)
            return Rules.TryGetValue(type, out rule);

        rule = null;
        return false;
    }

    internal static void EnsureDeclared(Share candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if(!TryGet(candidate.BlockType, out var rule))
            throw new ArgumentException(
                $"Block-only candidate type '{candidate.BlockType ?? "(none)"}' has no declared stable persistence identity and PostgreSQL conflict rule",
                nameof(candidate));

        if(string.IsNullOrWhiteSpace(candidate.PoolId) ||
           string.IsNullOrWhiteSpace(candidate.BlockHash))
            throw new ArgumentException(
                $"Block-only candidate type '{rule.Type}' requires its stable identity ({rule.StableIdentity})",
                nameof(candidate));

        if(string.Equals(candidate.BlockType, "auxpow-claim",
               StringComparison.Ordinal) &&
           string.IsNullOrWhiteSpace(candidate.TransactionConfirmationData))
            throw new ArgumentException(
                "Block-only auxpow-claim candidates require transactionConfirmationData as part of their stable identity",
                nameof(candidate));
    }
}
