namespace Miningcore.Mining;

// The semaphore serializes independent producers. Ambient ownership only detects
// recursion in the same logical operation; it never grants reentrant access.
internal sealed class WorkerAssignmentLease
{
    private static readonly AsyncLocal<WorkerAssignmentLease> current = new();
    private readonly WorkerContextBase worker;
    private WorkerAssignmentLease parent;
    private int released;
    private bool activated;

    internal WorkerAssignmentLease(WorkerContextBase worker) => this.worker = worker;

    internal static void ThrowIfReentrant(WorkerContextBase worker)
    {
        for(var owner = current.Value; owner != null; owner = owner.parent)
        {
            if(ReferenceEquals(owner.worker, worker) && Volatile.Read(ref owner.released) == 0)
                throw new InvalidOperationException("Worker difficulty assignment cannot re-enter its own gate");
        }
    }

    // Call synchronously in the owning async method AFTER awaiting acquisition.
    // AsyncLocal changes inside an async acquisition method do not flow back to
    // its caller. Activation here also survives awaits in publication callbacks.
    internal void Activate()
    {
        if(activated || Volatile.Read(ref released) != 0)
            throw new InvalidOperationException("Worker assignment lease is not available for activation");
        ThrowIfReentrant(worker);
        parent = current.Value;
        activated = true;
        current.Value = this;
    }

    internal void Release()
    {
        if(Interlocked.Exchange(ref released, 1) != 0)
            throw new InvalidOperationException("Worker assignment lease was already released");
        if(ReferenceEquals(current.Value, this))
            current.Value = parent;
        worker.AssignmentGate.Release();
    }
}
