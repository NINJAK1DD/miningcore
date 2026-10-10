namespace Miningcore.Mining;

// The semaphore serializes independent producers. Ambient ownership forbids any
// nested assignment: cross-worker nesting can invert independent lock orders.
// Background work inherits ownership and retains it until that work's context ends.
// Schedule detached work after release, or use ExecutionContext.SuppressFlow() only
// while scheduling it (restore flow before await); never await it under this lease.
internal sealed class WorkerAssignmentLease
{
    private static readonly AsyncLocal<WorkerAssignmentLease> current = new();
    private WorkerContextBase worker;
    private WorkerAssignmentLease parent;
    private int released;
    private bool activated;

    internal WorkerAssignmentLease(WorkerContextBase worker) => this.worker = worker;

    internal static void ThrowIfNestedAssignment(WorkerContextBase worker)
    {
        for(var owner = current.Value; owner != null; owner = owner.parent)
        {
            if(Volatile.Read(ref owner.released) == 0)
                throw new InvalidOperationException(ReferenceEquals(owner.worker, worker)
                    ? "Worker difficulty assignment cannot re-enter its own gate"
                    : "Worker difficulty assignments cannot nest across workers");
        }
    }

    // Call synchronously in the owning async method AFTER awaiting acquisition.
    // AsyncLocal changes inside an async acquisition method do not flow back to
    // its caller. Activation here also survives awaits in publication callbacks.
    internal void Activate()
    {
        if(activated || Volatile.Read(ref released) != 0)
            throw new InvalidOperationException("Worker assignment lease is not available for activation");
        ThrowIfNestedAssignment(worker);
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
        var ownedWorker = worker;
        // Captured background contexts may outlive release. Do not keep the
        // worker, its jobs and connection state alive through their stale lease.
        worker = null;
        ownedWorker.AssignmentGate.Release();
    }
}
