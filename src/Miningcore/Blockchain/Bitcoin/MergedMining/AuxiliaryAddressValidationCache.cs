namespace Miningcore.Blockchain.Bitcoin.MergedMining;

/// <summary>
/// Bounded process-local LRU of auxiliary addresses that were positively validated by the
/// configured daemon. It permits reconnects during a temporary daemon outage without trusting
/// an address this process has never verified.
/// </summary>
internal sealed class AuxiliaryAddressValidationCache
{
    public AuxiliaryAddressValidationCache(int capacity)
    {
        if(capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    private readonly int capacity;
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<string>> entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = new();

    internal int Count
    {
        get
        {
            lock(gate)
                return entries.Count;
        }
    }

    public bool Contains(string address)
    {
        if(string.IsNullOrWhiteSpace(address))
            return false;

        lock(gate)
        {
            if(!entries.TryGetValue(address, out var node))
                return false;

            recency.Remove(node);
            recency.AddFirst(node);
            return true;
        }
    }

    public void Add(string address)
    {
        if(string.IsNullOrWhiteSpace(address))
            return;

        lock(gate)
        {
            if(entries.TryGetValue(address, out var existing))
            {
                recency.Remove(existing);
                recency.AddFirst(existing);
                return;
            }

            var node = recency.AddFirst(address);
            entries.Add(address, node);

            if(entries.Count <= capacity)
                return;

            var expired = recency.Last;
            recency.RemoveLast();
            entries.Remove(expired.Value);
        }
    }

    public void Remove(string address)
    {
        if(string.IsNullOrWhiteSpace(address))
            return;

        lock(gate)
        {
            if(!entries.Remove(address, out var node))
                return;

            recency.Remove(node);
        }
    }
}
