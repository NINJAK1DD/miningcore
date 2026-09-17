namespace Miningcore.Api.Middlewares;

/// <summary>
/// Bounds informational logging with fixed-size, process-local state. Suppressed
/// entries are summarized the next time the interval permits an informational entry.
/// </summary>
internal sealed class MonotonicLogLimiter
{
    public MonotonicLogLimiter(TimeSpan interval,
        TimeProvider timeProvider = null)
    {
        if(interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        this.interval = interval;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    private readonly object gate = new();
    private readonly TimeSpan interval;
    private readonly TimeProvider timeProvider;
    private long previousInformationalTimestamp;
    private long suppressed;
    private bool hasInformationalEntry;

    public bool TryAcquire(out long suppressedSinceLastEntry)
    {
        lock(gate)
        {
            var now = timeProvider.GetTimestamp();

            if(!hasInformationalEntry ||
                timeProvider.GetElapsedTime(previousInformationalTimestamp,
                    now) >= interval)
            {
                suppressedSinceLastEntry = suppressed;
                suppressed = 0;
                previousInformationalTimestamp = now;
                hasInformationalEntry = true;
                return true;
            }

            // Saturate rather than wrap. State remains fixed-size because it is never
            // keyed by source address or any other attacker-controlled value.
            if(suppressed < long.MaxValue)
                suppressed++;

            suppressedSinceLastEntry = 0;
            return false;
        }
    }
}
