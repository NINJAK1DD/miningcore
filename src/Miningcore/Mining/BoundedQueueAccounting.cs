using System.Threading.Channels;

namespace Miningcore.Mining;

internal sealed class BoundedQueueAccounting<T>
{
    public BoundedQueueAccounting(int capacity)
    {
        if(capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
    }

    private readonly object gate = new();
    private int depth;
    private int highWatermark;
    private long overflowCount;

    public int Capacity { get; }
    public int Depth => Volatile.Read(ref depth);
    public int HighWatermark => Volatile.Read(ref highWatermark);
    public long OverflowCount => Interlocked.Read(ref overflowCount);

    public SharePersistenceQueueMetricsSnapshot GetSnapshot()
    {
        lock(gate)
        {
            return new SharePersistenceQueueMetricsSnapshot(depth,
                highWatermark, Capacity, overflowCount);
        }
    }

    public bool TryWrite(ChannelWriter<T> writer, T item)
    {
        ArgumentNullException.ThrowIfNull(writer);

        lock(gate)
        {
            if(writer.TryWrite(item))
            {
                depth++;

                if(depth > highWatermark)
                    highWatermark = depth;

                return true;
            }

            // Rejection can mean full or concurrently completed. Preserve the exact observed
            // occupancy while recording either condition in the monotonic rejection counter.
            Interlocked.Increment(ref overflowCount);
            return false;
        }
    }

    public bool TryRead(ChannelReader<T> reader, out T item)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock(gate)
        {
            if(!reader.TryRead(out item))
                return false;

            // Admission and removal use this same gate, so every visible channel item already
            // contributed exactly once to depth before a consumer can remove it.
            depth--;
            return true;
        }
    }
}
