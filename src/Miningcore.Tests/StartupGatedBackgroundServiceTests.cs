using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Miningcore.Tests;

public class StartupGatedBackgroundServiceTests
{
    [Theory]
    [InlineData(StartupOutcome.Ready)]
    [InlineData(StartupOutcome.ExecutionFailure)]
    [InlineData(StartupOutcome.MissingReadiness)]
    [InlineData(StartupOutcome.SignaledFailure)]
    [InlineData(StartupOutcome.Canceled)]
    public async Task StartAsync_CompletesWithoutPostingToCallerContext(StartupOutcome outcome)
    {
        using var service = new ControlledService(outcome);
        using var startupCancellation = new CancellationTokenSource();
        var context = new RecordingContext();
        var originalContext = SynchronizationContext.Current;
        Task startup;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            startup = service.StartAsync(startupCancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        // ExecuteAsync cannot signal or finish until StartAsync has returned its
        // incomplete task. This forces the readiness await to suspend, without sleeps.
        Assert.False(startup.IsCompleted);
        try
        {
            if(outcome == StartupOutcome.Canceled)
                startupCancellation.Cancel();
            else
                service.Release();

            var error = await Record.ExceptionAsync(() => startup.WaitAsync(TimeSpan.FromSeconds(10)));
            switch(outcome)
            {
                case StartupOutcome.Ready:
                    Assert.Null(error);
                    // Readiness must not wait for the entire background-service lifetime.
                    Assert.False(service.ExecuteTask.IsCompleted);
                    break;
                case StartupOutcome.ExecutionFailure:
                case StartupOutcome.SignaledFailure:
                    Assert.Same(service.Failure, error);
                    break;
                case StartupOutcome.MissingReadiness:
                    Assert.Contains("completed before signaling startup readiness",
                        Assert.IsType<InvalidOperationException>(error).Message);
                    break;
                case StartupOutcome.Canceled:
                    Assert.IsAssignableFrom<OperationCanceledException>(error);
                    break;
            }

            // An occupied caller context must not hold service readiness, errors,
            // or cancellation behind unrelated continuations (as in full-suite CI).
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            service.Release();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public enum StartupOutcome
    {
        Ready,
        ExecutionFailure,
        MissingReadiness,
        SignaledFailure,
        Canceled,
    }

    private sealed class ControlledService(StartupOutcome outcome) : StartupGatedBackgroundService
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InvalidOperationException Failure { get; } = new("controlled startup failure");
        public void Release() => release.TrySetResult();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await release.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            switch(outcome)
            {
                case StartupOutcome.ExecutionFailure:
                    throw Failure;
                case StartupOutcome.MissingReadiness:
                    return;
                case StartupOutcome.SignaledFailure:
                    SignalStartupFailure(Failure);
                    return;
                default:
                    SignalStartupReady();
                    await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int posts;
        public int PostCount => Volatile.Read(ref posts);

        public override void Post(SendOrPostCallback callback, object state)
        {
            Interlocked.Increment(ref posts);
            // Forward rather than dropping continuations: the unfixed implementation
            // fails the capture assertion promptly, without leaking a blocked startup.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
