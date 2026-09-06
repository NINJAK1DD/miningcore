using System;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Rpc;
using Xunit;

namespace Miningcore.Tests.Rpc;

public class RpcSubscriptionLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerRunsCleanupEvenWhenParentAlreadyCancelled(bool alreadyCancelled)
    {
        using var parent = new CancellationTokenSource();
        if(alreadyCancelled) parent.Cancel();
        var lifetime = new RpcSubscriptionLifetime(parent.Token);
        var ran = false;
        await lifetime.Run(() =>
        {
            ran = true;
            Assert.Equal(alreadyCancelled, lifetime.Token.IsCancellationRequested);
            return Task.CompletedTask;
        });
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ran);
        lifetime.Cancel(); // Late unsubscribe cannot touch a disposed source.
        parent.Cancel();
    }

    [Fact]
    public async Task WorkerCompletionWaitsForActiveCancellationBeforeDisposal()
    {
        var lifetime = new RpcSubscriptionLifetime(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var callback = lifetime.Token.Register(() =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var cancelling = Task.Run(lifetime.Cancel);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lifetime.Complete();
            Assert.False(lifetime.Completion.IsCompleted);
            lifetime.Cancel(); // Completion has closed new cancellation admission.
        }
        finally { release.Set(); }
        await cancelling.WaitAsync(TimeSpan.FromSeconds(10));
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AsyncCompletionWaitsWithoutBlockingWhileParentCallbackRuns()
    {
        using var parent = new CancellationTokenSource();
        var lifetime = new RpcSubscriptionLifetime(parent.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var callback = lifetime.Token.Register(() =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var cancelling = Task.Run(parent.Cancel);
        Task completing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            completing = lifetime.CompleteAsync().AsTask();
            Assert.False(completing.IsCompleted); // Calling CompleteAsync returned, rather than parking this thread.
            Assert.False(lifetime.Completion.IsCompleted);
        }
        finally { release.Set(); }
        await Task.WhenAll(cancelling, completing).WaitAsync(TimeSpan.FromSeconds(10));
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConcurrentParentCancellationUnsubscribeAndWorkerExitAreSafe()
    {
        for(var i = 0; i < 100; i++)
        {
            using var parent = new CancellationTokenSource();
            var lifetime = new RpcSubscriptionLifetime(parent.Token);
            await Task.WhenAll(Task.Run(parent.Cancel), Task.Run(lifetime.Cancel),
                lifetime.Run(() => Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(10));
            await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
