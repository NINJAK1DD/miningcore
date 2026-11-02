using System.Diagnostics;
using System.Numerics;
using Miningcore.Mining;
using Miningcore.Stratum;

namespace Miningcore.Blockchain.Pearl;

internal sealed class PearlJob
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal PearlBlockJob Block { get; init; }
    internal BigInteger Target { get; init; }
    internal double Difficulty { get; init; }
    internal long Issued { get; init; }
    internal long? Retired { get; set; }
    internal HashSet<string> Proofs { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Attempts { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Share> PendingShares { get; } = new(StringComparer.Ordinal);
}

internal sealed class PearlWorkerContext : WorkerContextBase
{
    private readonly LinkedList<PearlJob> jobs = new();
    private readonly object sync = new();
    private readonly TimeProvider time;
    private long lastProof;
    private bool receivedProof;
    public PearlWorkerContext() : this(TimeProvider.System) { }
    internal PearlWorkerContext(TimeProvider time) => this.time = time ?? TimeProvider.System;
    internal override double MinimumDifficulty => PearlWork.MinimumDifficulty;
    internal override double MaximumDifficulty => PearlWork.MaximumDifficulty;
    protected override double ValidateDifficulty(double value) { PearlWork.Target(value); return value; }

    internal PearlJob Issue(PearlBlockJob block, int maximum)
    {
        lock(sync)
        {
            var now = time.GetTimestamp();
            foreach(var previous in jobs)
                if(previous.Difficulty != Difficulty && !previous.Retired.HasValue)
                    previous.Retired = now;
            var job = new PearlJob {Block = block, Target = PearlWork.Target(Difficulty), Difficulty = Difficulty, Issued = now};
            jobs.AddFirst(job);
            while(jobs.Count > maximum) jobs.RemoveLast();
            return job;
        }
    }

    internal PearlJob Find(string id, string previousHash, int lifetimeSeconds, int graceSeconds)
    {
        lock(sync)
        {
            var now = time.GetTimestamp();
            var job = jobs.FirstOrDefault(x => x.Id == id);
            if(job == null || job.Block.PreviousBlockHash != previousHash ||
                time.GetElapsedTime(job.Issued, now).TotalSeconds >= lifetimeSeconds ||
                job.Retired.HasValue && time.GetElapsedTime(job.Retired.Value, now).TotalSeconds >= graceSeconds)
                throw new StratumException(StratumError.JobNotFound, "Stale Pearl job");
            return job;
        }
    }

    internal bool TakeProofBudget()
    {
        lock(sync)
        {
            var now = time.GetTimestamp();
            if(receivedProof && time.GetElapsedTime(lastProof, now) < TimeSpan.FromSeconds(1)) return false;
            receivedProof = true;
            lastProof = now;
            return true;
        }
    }

    internal void Remember(PearlJob job, string identity)
    {
        lock(sync)
        {
            if(job.Proofs.Contains(identity)) throw new StratumException(StratumError.DuplicateShare, "Duplicate Pearl share");
            if(job.Proofs.Count >= 256) throw new StratumException(StratumError.Other, "Pearl job share capacity reached");
            job.Proofs.Add(identity);
        }
    }

    internal void RememberAttempt(PearlJob job, string canonicalProofHash)
    {
        lock(sync)
        {
            if(job.Attempts.Contains(canonicalProofHash))
                throw new StratumException(StratumError.DuplicateShare, "Duplicate Pearl proof");
            if(job.Attempts.Count >= 256)
                throw new StratumException(StratumError.Other, "Pearl job verification capacity reached");
            job.Attempts.Add(canonicalProofHash);
        }
    }

    internal (int Active, int Stale) JobCounts(string tip, int lifetimeSeconds, int graceSeconds)
    {
        lock(sync)
        {
            var now = time.GetTimestamp();
            var active = jobs.Count(job => job.Block.PreviousBlockHash == tip &&
                time.GetElapsedTime(job.Issued, now).TotalSeconds < lifetimeSeconds &&
                (!job.Retired.HasValue || time.GetElapsedTime(job.Retired.Value, now).TotalSeconds < graceSeconds));
            return (active, jobs.Count - active);
        }
    }

    internal void Forget(PearlJob job, string identity) { lock(sync) job.Proofs.Remove(identity); }
    internal void ForgetAttempt(PearlJob job, string hash) { lock(sync) job.Attempts.Remove(hash); }

    internal async Task CommitAsync(PearlJob job, string attempt, string identity, Share share, Func<Share, Task> commit)
    {
        lock(sync)
        {
            // Reserve before adding retry metadata. A duplicate/capacity refusal
            // must leave the original pending payload and reservations untouched.
            if(job.Proofs.Contains(identity)) throw new StratumException(StratumError.DuplicateShare, "Duplicate Pearl share");
            if(job.Proofs.Count >= 256) throw new StratumException(StratumError.Other, "Pearl job share capacity reached");
            if(job.PendingShares.TryGetValue(identity, out var original)) share = original;
            else
            {
                if(job.PendingShares.Count >= 256)
                    throw new StratumException(StratumError.Other, "Pearl pending receipt capacity reached");
                job.PendingShares.Add(identity, share);
            }
            job.Proofs.Add(identity);
        }
        try
        {
            await commit(share);
            lock(sync) job.PendingShares.Remove(identity);
        }
        catch
        {
            // Retain the immutable payload (including Created and PPS evidence).
            // Retrying the same accounting ID is safe even if COMMIT was ambiguous.
            Forget(job, identity);
            ForgetAttempt(job, attempt);
            throw;
        }
    }
}
