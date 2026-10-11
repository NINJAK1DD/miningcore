namespace Miningcore.Mining;

// Shared by jobs issued during one assignment. Retirement is one-way: a later
// assignment, including returning to the same value, cannot renew old work.
internal sealed class IssuedDifficulty(double difficulty, TimeProvider timeProvider, object session)
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private long? retiredAt;
    internal double Difficulty { get; } = difficulty;
    internal object Session { get; } = session;

    internal void Retire()
    {
        lock(this)
            retiredAt ??= timeProvider.GetTimestamp();
    }

    internal bool TryValidate(double proofDifficulty, bool blockCandidate, out double credit)
    {
        credit = Difficulty;
        if(!double.IsFinite(credit) || credit <= 0 || double.IsNaN(proofDifficulty) || proofDifficulty <= 0)
            return false;

        lock(this)
        {
            if(!blockCandidate && retiredAt is { } timestamp)
            {
                var now = timeProvider.GetTimestamp();
                var elapsed = timeProvider.GetElapsedTime(timestamp, now);
                if(now < timestamp || elapsed < TimeSpan.Zero || elapsed >= Grace)
                    return false;
            }
        }

        return blockCandidate || proofDifficulty / credit >= 0.99;
    }
}
