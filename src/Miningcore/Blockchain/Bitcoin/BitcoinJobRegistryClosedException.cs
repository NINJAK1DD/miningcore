namespace Miningcore.Blockchain.Bitcoin;

/// <summary>
/// The worker's job registry is permanently closed. Work construction must stop;
/// clearing jobs or changing payout authorization cannot reopen the registry.
/// </summary>
public sealed class BitcoinJobRegistryClosedException : OperationCanceledException
{
    public BitcoinJobRegistryClosedException() : base("Worker job registry is closed") { }
}
