using Miningcore.Blockchain;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// Job commitments contain a random 128-bit discriminator across processes and
// restarts. Within a process, never recycle a connection's four-byte suffix.
internal sealed class BitcoinBlake2bExtraNonceProvider : IExtraNonceProvider
{
    private long counter;

    public int ByteSize => 4;

    public string Next()
    {
        var value = Interlocked.Increment(ref counter);
        if(value > uint.MaxValue)
            throw new InvalidOperationException("Bitcoin BLAKE2b connection extranonce space exhausted; restart the pool to create new job commitments");
        return value.ToString("x8");
    }
}
