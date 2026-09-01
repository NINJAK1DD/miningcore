using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using Miningcore.Mining;
using NBitcoin;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinWorkerContext : WorkerContextBase
{
    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public override string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identififer for miners using multiple rigs
    /// </summary>
    public override string Worker { get; set; }

    /// <summary>
    /// Current stratum session / connection id
    /// </summary>
    public string SessionId { get; set; }

    /// <summary>
    /// Unique value assigned per worker
    /// </summary>
    public string ExtraNonce1 { get; set; }

    /// <summary>
    /// Mask for version-rolling (Overt ASIC-Boost)
    /// </summary>
    public uint? VersionRollingMask { get; internal set; }

    /// <summary>
    /// Immutable destination snapshot established by successful direct-SOLO
    /// authorization. Existing jobs retain their own copy.
    /// </summary>
    public IDestination DirectPayoutDestination { get; internal set; }
    public string DirectPayoutAddress { get; internal set; }

    /// <summary>
    /// Current N job(s) assigned to this worker
    /// </summary>
    public Queue<BitcoinJob> validJobs { get; private set; } = new();

    public virtual void AddJob(BitcoinJob job, int maxActiveJobs)
    {
        if(!validJobs.Contains(job))
            validJobs.Enqueue(job);

        while(validJobs.Count > maxActiveJobs)
            validJobs.Dequeue();
    }

    public BitcoinJob GetJob(string jobId)
    {
        return validJobs.ToArray().FirstOrDefault(x => x.JobId == jobId);
    }

    public void ClearJobs()
    {
        validJobs.Clear();
    }
}
