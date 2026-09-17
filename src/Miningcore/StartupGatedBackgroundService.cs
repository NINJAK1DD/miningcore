using Microsoft.Extensions.Hosting;

namespace Miningcore;

/// <summary>
/// Prevents later hosted services from starting until ExecuteAsync has completed
/// the subscriptions or other setup required to receive their events.
/// </summary>
public abstract class StartupGatedBackgroundService : BackgroundService
{
    private readonly TaskCompletionSource startupReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    protected void SignalStartupReady()
    {
        startupReady.TrySetResult();
    }

    protected bool SignalStartupFailure(Exception ex)
    {
        return startupReady.TrySetException(ex);
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Readiness has no caller-context affinity. A busy synchronization context
        // must not delay reporting background startup completion or failure.
        await base.StartAsync(ct).ConfigureAwait(false);

        // .NET 10 schedules ExecuteAsync entirely in the background. Observe both tasks so
        // an early ExecuteAsync fault or return cannot leave host startup waiting forever.
        var executeTask = ExecuteTask ?? throw new InvalidOperationException(
            $"{GetType().Name} did not create a background execution task");
        var completed = await Task.WhenAny(startupReady.Task, executeTask).WaitAsync(ct).ConfigureAwait(false);

        if(completed == executeTask)
        {
            await executeTask.ConfigureAwait(false);

            if(!startupReady.Task.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} completed before signaling startup readiness");
            }
        }

        await startupReady.Task.ConfigureAwait(false);
    }
}
