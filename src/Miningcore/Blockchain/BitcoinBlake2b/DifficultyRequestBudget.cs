namespace Miningcore.Blockchain.BitcoinBlake2b;

/// <summary>
/// Connection-local token bucket. Measures elapsed time, never miner timestamps
/// or wall time. No timer, queue, IP-address map, or background task is allocated.
/// </summary>
internal sealed class DifficultyRequestBudget
{
    internal const int Capacity = 8;
    internal const int DisconnectAfterRefusals = 8;
    internal static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(10);

    internal enum Admission { Allowed, Refused, Disconnect }

    internal DifficultyRequestBudget(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        previousTimestamp = timeProvider.GetTimestamp();
    }

    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private long previousTimestamp;
    private double tokens = Capacity;
    private int refusals;
    private int closed;
    private int duplicateSubscribeWarning;

    internal bool IsClosed => Volatile.Read(ref closed) != 0;
    internal bool TryClose() => Interlocked.Exchange(ref closed, 1) == 0;
    internal bool TryWarnDuplicateSubscribe() => Interlocked.Exchange(ref duplicateSubscribeWarning, 1) == 0;

    internal Admission TryAcquire()
    {
        lock(gate)
        {
            // A disconnect decision is terminal even if buffered requests are
            // dispatched while the socket teardown is still completing.
            if(IsClosed || refusals >= DisconnectAfterRefusals)
                return Admission.Disconnect;

            var now = timeProvider.GetTimestamp();
            var elapsed = timeProvider.GetElapsedTime(previousTimestamp, now);
            if(elapsed > TimeSpan.Zero)
            {
                tokens = Math.Min(Capacity, tokens + elapsed.TotalSeconds / RefillInterval.TotalSeconds);
                previousTimestamp = now;
            }

            if(tokens >= 1)
            {
                tokens--;
                refusals = 0;
                return Admission.Allowed;
            }

            // Bound refusal responses too. No per-request warning/log payload.
            if(refusals < DisconnectAfterRefusals)
                refusals++;
            return refusals >= DisconnectAfterRefusals ? Admission.Disconnect : Admission.Refused;
        }
    }
}
