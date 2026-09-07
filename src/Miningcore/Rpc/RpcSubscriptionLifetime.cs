namespace Miningcore.Rpc;

// The worker completes ownership; unsubscribe and parent cancellation only request
// cancellation. Disposal waits for both worker exit and all active Cancel calls.
internal sealed class RpcSubscriptionLifetime
{
    private readonly object gate = new();
    private readonly CancellationTokenSource source = new();
    private readonly CancellationTokenRegistration parentRegistration;
    private readonly Action onCancellationCallbackFailure;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int cancelling;
    private bool finished;

    internal RpcSubscriptionLifetime(CancellationToken parent, Action onCancellationCallbackFailure = null)
    {
        this.onCancellationCallbackFailure = onCancellationCallbackFailure;
        Token = source.Token;
        parentRegistration = parent.Register(Cancel);
    }

    internal CancellationToken Token { get; }
    internal Task Completion => completion.Task;

    // No scheduling token: even pre-cancelled work must release its ownership.
    internal Task Run(Func<Task> worker) => Task.Run(async () =>
    {
        try { await worker(); }
        finally { await CompleteAsync(); }
    });

    internal void Cancel()
    {
        lock(gate)
        {
            if(finished) return;
            cancelling++;
        }
        try { source.Cancel(throwOnFirstException: false); }
        catch(AggregateException)
        {
            // All callbacks have run and cancellation is already set. Their errors
            // must not interrupt parent shutdown or Rx unsubscribe. Report only a
            // fixed signal: callback exceptions can contain credentials/payloads.
            try { onCancellationCallbackFailure?.Invoke(); }
            catch(Exception) { /* Best-effort reporting must not break cleanup either. */ }
        }
        finally
        {
            lock(gate)
            {
                cancelling--;
                if(finished && cancelling == 0) Release();
            }
        }
    }

    internal void Complete()
    {
        parentRegistration.Dispose();
        Finish();
    }

    internal async ValueTask CompleteAsync()
    {
        await parentRegistration.DisposeAsync();
        Finish();
    }

    private void Finish()
    {
        lock(gate)
        {
            if(finished) return;
            finished = true;
            if(cancelling == 0) Release();
        }
    }

    private void Release()
    {
        source.Dispose();
        completion.TrySetResult();
    }
}
