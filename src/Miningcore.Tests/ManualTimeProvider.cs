using System;
using System.Threading;

namespace Miningcore.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset? utcNow = null)
    {
        this.utcNow = utcNow ?? new DateTimeOffset(2026, 8, 15, 12, 0, 0,
            TimeSpan.Zero);
    }

    private readonly object wallClockGate = new();
    private long timestamp;
    private DateTimeOffset utcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);

    public override DateTimeOffset GetUtcNow()
    {
        lock(wallClockGate)
            return utcNow;
    }

    public void AdvanceMonotonic(TimeSpan elapsed) =>
        Interlocked.Add(ref timestamp, elapsed.Ticks);

    public void MoveWallClock(TimeSpan change)
    {
        lock(wallClockGate)
            utcNow = utcNow.Add(change);
    }
}
