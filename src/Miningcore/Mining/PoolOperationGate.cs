namespace Miningcore.Mining;

/// <summary>
/// Optional, pool-local work isolation. This does not replace the process-wide
/// persistence admission gate or change financial-failure shutdown policy.
/// </summary>
public interface IIsolatedMiningPool
{
    string MiningState { get; }
    // Null means admission is closed. A successful lease must cover the complete
    // asynchronous operation, including persistence/outcome handling, and be disposed.
    IDisposable TryAcquireOperation();
}

internal sealed class PoolOperationGate
{
    private readonly object sync = new();
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closed;
    private int active;

    internal bool IsClosed { get { lock(sync) return closed; } }
    internal Task Failure => failure.Task;
    internal Task Drained => drained.Task;

    internal IDisposable TryAcquire()
    {
        lock(sync)
        {
            if(closed)
                return null;
            active++;
            return new Lease(this);
        }
    }

    internal bool Close()
    {
        lock(sync)
        {
            if(closed)
                return false;
            closed = true;
            failure.TrySetResult();
            if(active == 0)
                drained.TrySetResult();
            return true;
        }
    }

    private void Release()
    {
        lock(sync)
        {
            active--;
            if(closed && active == 0)
                drained.TrySetResult();
        }
    }

    private sealed class Lease(PoolOperationGate owner) : IDisposable
    {
        private PoolOperationGate owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
