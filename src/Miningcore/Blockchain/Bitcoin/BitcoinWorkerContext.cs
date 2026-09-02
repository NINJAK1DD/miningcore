using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Mining;
using NBitcoin;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinWorkerContext : WorkerContextBase
{
    internal sealed record DirectPayoutAuthorization(string Address,
        IDestination Destination, long Generation);

    private DirectPayoutAuthorization directPayoutAuthorization;
    private long directPayoutGeneration;
    private readonly SemaphoreSlim directPayoutOperationGate = new(1, 1);

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
    public IDestination DirectPayoutDestination =>
        GetDirectPayoutAuthorization()?.Destination;
    public string DirectPayoutAddress =>
        GetDirectPayoutAuthorization()?.Address;

    internal DirectPayoutAuthorization GetDirectPayoutAuthorization()
    {
        lock(this)
            return directPayoutAuthorization;
    }

    internal DirectPayoutAuthorization SetDirectPayoutAuthorization(
        string address, IDestination destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(destination);

        lock(this)
        {
            directPayoutGeneration = checked(directPayoutGeneration + 1);
            validJobs.Clear();
            directPayoutAuthorization = new(address, destination,
                directPayoutGeneration);
            return directPayoutAuthorization;
        }
    }

    internal async Task<DirectPayoutAuthorization>
        SetDirectPayoutAuthorizationAsync(string address,
            IDestination destination, CancellationToken ct)
    {
        await directPayoutOperationGate.WaitAsync(ct);
        try
        {
            return SetDirectPayoutAuthorization(address, destination);
        }
        finally
        {
            directPayoutOperationGate.Release();
        }
    }

    internal Task EnterDirectPayoutSubmissionAsync(CancellationToken ct) =>
        directPayoutOperationGate.WaitAsync(ct);

    internal void ExitDirectPayoutSubmission() =>
        directPayoutOperationGate.Release();

    /// <summary>
    /// Current N job(s) assigned to this worker
    /// </summary>
    public Queue<BitcoinJob> validJobs { get; private set; } = new();

    public virtual void AddJob(BitcoinJob job, int maxActiveJobs)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock(this)
            AddJobCore(job, maxActiveJobs);
    }

    internal bool TryAddDirectJob(BitcoinJob job, int maxActiveJobs)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock(this)
        {
            if(directPayoutAuthorization == null ||
               job.DirectPayoutGeneration !=
               directPayoutAuthorization.Generation)
                return false;

            AddJobCore(job, maxActiveJobs);
            return true;
        }
    }

    private void AddJobCore(BitcoinJob job, int maxActiveJobs)
    {
        if(!validJobs.Contains(job))
            validJobs.Enqueue(job);

        while(validJobs.Count > maxActiveJobs)
            validJobs.Dequeue();
    }

    public BitcoinJob GetJob(string jobId)
    {
        lock(this)
        {
            var job = validJobs.ToArray().FirstOrDefault(x =>
                x.JobId == jobId);

            if(job?.DirectPayoutGeneration is { } generation &&
               (directPayoutAuthorization == null || generation !=
                   directPayoutAuthorization.Generation))
                return null;

            return job;
        }
    }

    public void ClearJobs()
    {
        lock(this)
            validJobs.Clear();
    }
}
