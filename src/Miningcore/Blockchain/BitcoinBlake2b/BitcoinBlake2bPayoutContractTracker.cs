using System.Collections.Concurrent;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// The payout manager resolves/reconfigures handlers on each cycle. Keep the
// first verified contract in the process scope rather than the handler scope.
// Changing a pool's chain/schedule requires the documented stop/restart.
public sealed class BitcoinBlake2bPayoutContractTracker
{
    private readonly ConcurrentDictionary<string, (string Chain, BitcoinBlake2bMaturity Schedule)> contracts = new(StringComparer.Ordinal);
    internal const int CreationEvidenceCapacity = 32768;
    private readonly int capacity;
    private readonly object evidenceLock = new();
    private readonly LinkedList<CreationEvidence> recency = new();
    private readonly Dictionary<(string Pool, string Hash), LinkedListNode<CreationEvidence>> evidence = new();
    private sealed record CreationEvidence(string Pool, string Hash, uint Height, string ParentHash, long ParentMtp);

    public BitcoinBlake2bPayoutContractTracker() : this(CreationEvidenceCapacity) { }
    internal BitcoinBlake2bPayoutContractTracker(int capacity)
    {
        if(capacity is < 1 or > CreationEvidenceCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }
    internal int CachedCreationCount { get { lock(evidenceLock) return evidence.Count; } }

    internal void Attest(string poolId, string chain, BitcoinBlake2bMaturity schedule)
    {
        var observed = (chain, schedule);
        if(contracts.GetOrAdd(poolId, observed) != observed)
            throw new InvalidOperationException("Bitcoin BLAKE2b payout chain or maturity contract changed; stop and reconcile before restarting");
    }

    // Only immutable hash-linked creation data is cached, never confirmations,
    // active membership, wallet categories, spendability or accounting state.
    internal bool TryCreation(string pool, BitcoinBlake2bBlockHeader header, out long parentMtp, out bool conflict)
    {
        lock(evidenceLock)
        {
            parentMtp = 0;
            conflict = false;
            if(!evidence.TryGetValue((pool, header.Hash.ToLowerInvariant()), out var node)) return false;
            conflict = node.Value.Height != header.Height || !string.Equals(node.Value.ParentHash, header.PreviousBlockHash, StringComparison.OrdinalIgnoreCase);
            if(conflict) return false;
            recency.Remove(node);
            recency.AddLast(node);
            parentMtp = node.Value.ParentMtp;
            return true;
        }
    }

    internal void RememberCreation(string pool, BitcoinBlake2bBlockHeader header, long parentMtp)
    {
        lock(evidenceLock)
        {
            var key = (pool, header.Hash.ToLowerInvariant());
            if(evidence.ContainsKey(key)) return;
            if(evidence.Count == capacity)
            {
                var oldest = recency.First!;
                evidence.Remove((oldest.Value.Pool, oldest.Value.Hash));
                recency.RemoveFirst();
            }
            evidence.Add(key, recency.AddLast(new CreationEvidence(pool, key.Item2, header.Height, header.PreviousBlockHash, parentMtp)));
        }
    }

    internal void ForgetCreation(string pool, string hash)
    {
        if(hash == null) return;
        lock(evidenceLock)
            if(evidence.Remove((pool, hash.ToLowerInvariant()), out var node)) recency.Remove(node);
    }
}
