namespace Miningcore.Mining;

// The immutable candidate target in validator units, before stored-share scaling.
internal interface ICandidateCreditJob
{
    double CandidateDifficulty { get; }
}

internal static class CandidateShareCredit
{
    internal static double Limit(object job) => job switch
    {
        ICandidateCreditJob candidate => candidate.CandidateDifficulty,
        ValueTuple<object, uint> blob => Limit(blob.Item1),
        _ => double.MaxValue
    };

    // Keep negotiated/static assignments intact. A configured floor above the
    // chain target has no compatible automatic range and must be operator-fixed.
    internal static bool BoundAutomaticAssignment(WorkerContextBase context, double limit)
    {
        Cap(context.Difficulty, limit); // Validate before changing deferred work.
        var options = context.VarDiff?.Config;
        if(options == null)
            return true;
        var ceiling = Math.Min(limit, Math.Min(context.MaximumDifficulty, options.MaxDiff ?? double.MaxValue));
        var floor = Math.Max(context.MinimumDifficulty, options.MinDiff);
        if(context.RequiresIntegerDifficulty)
        {
            floor = Math.Ceiling(floor);
            ceiling = Math.Floor(ceiling);
        }
        if(floor > ceiling || ceiling <= 0)
        {
            // A queued automatic increase is no longer eligible. Preserve the
            // active assignment/floor, but do not publish deferred oversized work.
            context.SetDifficulty(context.Difficulty);
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
