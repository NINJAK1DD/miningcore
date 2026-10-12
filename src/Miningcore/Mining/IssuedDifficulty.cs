namespace Miningcore.Mining;

// Shared by jobs issued during one assignment. Retirement is one-way: a later
// assignment, including returning to the same value, cannot renew old work.
internal sealed class IssuedDifficulty(double difficulty, TimeProvider timeProvider, object session, TimeSpan? grace = null)
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly long issuedAt = timeProvider.GetTimestamp();
    private long? retiredAt;
    internal double Difficulty { get; } = difficulty;
    internal object Session { get; } = session;

    internal void Retire()
    {
        lock(sync)
            retiredAt ??= timeProvider.GetTimestamp();
    }

    internal bool TryValidate(double proofDifficulty, bool blockCandidate, out double credit, ShareReceipt? receipt = null)
    {
        credit = Difficulty;
        if(!double.IsFinite(credit) || credit <= 0 || double.IsNaN(proofDifficulty) || proofDifficulty <= 0)
            return false;

        lock(sync)
        {
            var now = timeProvider.GetTimestamp();
            var receivedAt = receipt?.Timestamp ?? now;
            // Only server-owned timestamps on this assignment's clock may grant grace.
            if(now < issuedAt || receivedAt < issuedAt || receivedAt > now ||
                receipt is { } received && !ReferenceEquals(received.Clock, timeProvider))
                return false;
            if(!blockCandidate && retiredAt is { } timestamp)
            {
                if(now < timestamp)
                    return false;
                if(receivedAt >= timestamp && timeProvider.GetElapsedTime(timestamp, receivedAt) >= (grace ?? Grace))
                    return false;
            }
        }

        return blockCandidate || proofDifficulty / credit >= 0.99;
    }
}

// Created by the transport, never read from JSON or miner-supplied timestamps.
internal readonly record struct ShareReceipt(TimeProvider Clock, long Timestamp);
