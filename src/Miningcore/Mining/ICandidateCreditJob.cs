namespace Miningcore.Mining;

// The immutable candidate target in validator units, before stored-share scaling.
internal interface ICandidateCreditJob
{
    double CandidateDifficulty { get; }
}

internal static class CandidateShareCredit
{
    private sealed record Policy(double Difficulty);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Policy> policies = new();

    // Validate at template construction, before managers replace their current work.
    // Derived initializers may refine the target before publication. Each completed
    // initializer caches its final value; worker snapshots share the same record.
    internal static void Initialize(object job)
    {
        if(job is not ICandidateCreditJob candidate)
            return;
        var difficulty = candidate.CandidateDifficulty;
        Cap(1, difficulty);
        policies.AddOrUpdate(job, new(difficulty));
    }

    private static Policy Read(object job) => policies.GetValue(job, static value =>
    {
        if(value is ICandidateCreditJob candidate)
        {
            var difficulty = candidate.CandidateDifficulty;
            Cap(1, difficulty);
            return new(difficulty);
        }
        // Bitcoin, merged Bitcoin and BLAKE2b have their own audited chain policy.
        if(value is Miningcore.Blockchain.Bitcoin.BitcoinJob)
            return new(double.MaxValue);
        throw new InvalidDataException($"No reviewed candidate-credit policy for {value.GetType().FullName}");
    });

    internal static double Limit(object job) => job is ValueTuple<object, uint> blob
        ? Limit(blob.Item1) : Read(job).Difficulty;

    internal static void Prepare(object job) => _ = Read(job);

    internal static void Copy(object template, object snapshot)
    {
        if(template is not ICandidateCreditJob || ReferenceEquals(template, snapshot))
            return;
        var policy = Read(template);
        policies.Add(snapshot, policy);
    }

    // Keep negotiated/static assignments intact. A configured floor above the
    // chain target has no compatible automatic range and must be operator-fixed.
    internal static bool BoundAutomaticAssignment(WorkerContextBase context, double limit)
    {
        Cap(context.Difficulty, limit); // Validate before changing deferred work.
        var options = context.VarDiff?.Config;
        if(options == null)
            return true;
        // A miner request is not a deferred VarDiff retarget. Its floor is
        // committed atomically with the requested assignment by ApplyPendingDifficulty.
        if(context.HasPendingDifficulty && context.PendingDifficultyOrigin != DifficultyOrigin.Automatic)
            return true;
        var maximum = Math.Min(context.MaximumDifficulty, options.MaxDiff ?? double.MaxValue);
        var ceiling = Math.Min(limit, Math.Min(context.MaximumDifficulty, options.MaxDiff ?? double.MaxValue));
        var floor = Math.Max(context.MinimumVarDiffDifficulty, options.MinDiff);
        if(context.RequiresIntegerDifficulty)
        {
            floor = Math.Ceiling(floor);
            ceiling = Math.Floor(ceiling);
        }
        if(floor > ceiling || ceiling <= 0)
        {
            // A queued automatic increase is no longer eligible. Preserve the
            // active assignment/floor, but do not publish deferred oversized work.
            context.DiscardPendingDifficulty();
            // The chain ceiling overrides MaxDelta smoothing, never the floor.
            // Select the least excessive legal assignment instead of freezing a
            // worker far above both the floor and the new network threshold.
            if(context.Difficulty > floor && floor > 0 && floor <= maximum)
                context.EnqueueNewDifficulty(floor);
            return false;
        }
        if((context.PendingDifficulty ?? context.Difficulty) > ceiling)
            context.EnqueueNewDifficulty(ceiling);
        return true;
    }

    internal static double Cap(double issuedCredit, double candidateDifficulty)
    {
        if(!double.IsFinite(issuedCredit) || issuedCredit <= 0 ||
           !double.IsFinite(candidateDifficulty) || candidateDifficulty <= 0)
            throw new InvalidDataException("Candidate credit requires finite positive issued and chain difficulty");
        // Apply even when the proof exceeds the assignment: exception-only caps
        // would reward selective submission of exceptionally strong candidates.
        return Math.Min(issuedCredit, candidateDifficulty);
    }
}
