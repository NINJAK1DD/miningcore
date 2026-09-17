using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Miningcore.Messaging;
using NLog;

namespace Miningcore.Mining;

public interface IMiningFailStopCoordinator
{
    bool IsFailStopRequested { get; }
    CancellationToken Token { get; }
    IMiningSubmissionAcceptance AcquireSubmissionAcceptance();
    bool BeginFailStop(int exitCode);
}

public interface IMiningSubmissionAcceptance : IDisposable
{
    void PublishShare(IMessageBus messageBus, Blockchain.Share share,
        string contract = null);
    void QueueResponse(Action queueResponse);
}

internal interface IMiningAdmissionFailure
{
    Task HandleAfterAdmissionReleasedAsync();
}

internal interface IMiningFailStopCaptureCoordinator
{
    T BeginFailStopAndCapture<T>(int exitCode, Func<T> capture);
}

internal static class MiningFailStopCaptureExtensions
{
    public static T BeginFailStopAndCapture<T>(
        this IMiningFailStopCoordinator coordinator, int exitCode,
        Func<T> capture)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(capture);

        if(coordinator is IMiningFailStopCaptureCoordinator exclusive)
            return exclusive.BeginFailStopAndCapture(exitCode, capture);

        // Compatibility for test/third-party coordinators. Production uses the exclusive
        // implementation below, which executes capture while holding the admission write gate.
        coordinator.BeginFailStop(exitCode);
        return capture();
    }
}

public sealed class MiningFailStopCoordinator : IMiningFailStopCoordinator,
    IMiningFailStopCaptureCoordinator, IDisposable
{
    public MiningFailStopCoordinator(IProcessStatus processStatus,
        IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentNullException.ThrowIfNull(applicationLifetime);

        this.processStatus = processStatus;
        this.applicationLifetime = applicationLifetime;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IProcessStatus processStatus;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly CancellationTokenSource failStop = new();
    private readonly ReaderWriterLockSlim acceptanceGate =
        new(LockRecursionPolicy.NoRecursion);
    private int failStopRequested;

    public bool IsFailStopRequested =>
        Volatile.Read(ref failStopRequested) != 0;

    public CancellationToken Token => failStop.Token;

    public IMiningSubmissionAcceptance AcquireSubmissionAcceptance()
    {
        if(acceptanceGate.IsReadLockHeld)
            throw new InvalidOperationException(
                "Recursive share publication is not supported while mining admission is held. " +
                "A Share subscriber must not republish the same Share synchronously.");

        acceptanceGate.EnterReadLock();

        try
        {
            ThrowIfFailStopRequested();
            return new MiningSubmissionAcceptance(this);
        }
        finally
        {
            acceptanceGate.ExitReadLock();
        }
    }

    public bool BeginFailStop(int exitCode)
    {
        var result = BeginFailStopAndCaptureCore(exitCode, () => true);
        return result.Started;
    }

    T IMiningFailStopCaptureCoordinator.BeginFailStopAndCapture<T>(int exitCode,
        Func<T> capture) => BeginFailStopAndCaptureCore(exitCode, capture).Value;

    private (bool Started, T Value) BeginFailStopAndCaptureCore<T>(int exitCode,
        Func<T> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        // ProcessStatus deliberately permits the non-restart durability-loss status to
        // upgrade an earlier general failure. Every invocation must reach it, including
        // failures discovered after shutdown has already started.
        processStatus.MarkFailed(exitCode);
        var started = false;
        T value = default;
        ExceptionDispatchInfo captureFailure = null;

        acceptanceGate.EnterWriteLock();

        try
        {
            started = Interlocked.Exchange(ref failStopRequested, 1) == 0;

            if(started)
            {
                // Cancellation happens while holding the same gate used by share publication and
                // response admission. Once this returns, no check-then-queue race can acknowledge
                // a share that did not first enter the accounting pipeline.
                failStop.Cancel();
            }

            // All earlier publication/response readers have now drained and no later reader can
            // enter. Fatal recovery evidence must be captured at this exact boundary.
            try
            {
                value = capture();
            }
            catch(Exception ex)
            {
                captureFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }
        finally
        {
            acceptanceGate.ExitWriteLock();
        }

        if(started)
        {
            try
            {
                applicationLifetime.StopApplication();
            }
            catch(Exception ex)
            {
                // The synchronous share and response gates are already closed. Preserve the original
                // incident path so it can still write the latch and attempt its critical alert.
                logger.Fatal(ex,
                    "Host shutdown signalling failed after the mining fail-stop gates closed");
            }
        }

        captureFailure?.Throw();
        return (started, value);
    }

    private void PublishShare(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);

        ExceptionDispatchInfo failure = null;
        IMiningAdmissionFailure deferred = null;
        acceptanceGate.EnterReadLock();

        try
        {
            ThrowIfFailStopRequested();
            publish();
        }
        catch(Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
            deferred = ex as IMiningAdmissionFailure;
        }
        finally
        {
            acceptanceGate.ExitReadLock();
        }

        // A bounded persistence queue can discover journal failure from inside a synchronous
        // publication callback. Run its fail-stop transition only after releasing this admission's
        // read lock so the exclusive transition cannot deadlock behind the reporting submission.
        if(deferred != null)
        {
            try
            {
                // The handler closes admission and captures the exact unresolved boundary before
                // returning its Task. Observe the slower fatal-state and notification work without
                // blocking a Stratum thread for filesystem sync or the alert timeout.
                var handling = deferred.HandleAfterAdmissionReleasedAsync();
                _ = handling.ContinueWith(task =>
                    logger.Fatal(task.Exception,
                        "Deferred mining fail-stop handling faulted after admission was closed"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch(Exception ex)
            {
                // Admission is expected to be closed synchronously by the handler. Preserve the
                // publication failure while making any unexpected handoff failure visible.
                logger.Fatal(ex,
                    "Unable to hand off deferred mining fail-stop handling");
            }
        }

        failure?.Throw();
    }

    private void PublishShare(IMessageBus messageBus, Blockchain.Share share,
        string contract)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(share);

        if(messageBus is not IMiningAdmissionMessageBus admittedMessageBus)
            throw new InvalidOperationException(
                "The configured message bus does not support admission-owned share publication");

        PublishShare(() => admittedMessageBus.SendMessageWithinMiningAdmission(share,
            contract));
    }

    private void QueueResponse(Action queueResponse)
    {
        ArgumentNullException.ThrowIfNull(queueResponse);

        acceptanceGate.EnterReadLock();

        try
        {
            ThrowIfFailStopRequested();
            queueResponse();
        }
        finally
        {
            acceptanceGate.ExitReadLock();
        }
    }

    private void ThrowIfFailStopRequested()
    {
        if(IsFailStopRequested)
            throw new OperationCanceledException(
                "Mining share acceptance is closed by the fail-stop gate", Token);
    }

    public void Dispose()
    {
        failStop.Dispose();
        acceptanceGate.Dispose();
    }

    private sealed class MiningSubmissionAcceptance : IMiningSubmissionAcceptance
    {
        public MiningSubmissionAcceptance(MiningFailStopCoordinator owner)
        {
            this.owner = owner;
        }

        private readonly MiningFailStopCoordinator owner;
        private int disposed;

        public void PublishShare(IMessageBus messageBus, Blockchain.Share share,
            string contract = null)
        {
            ThrowIfDisposed();
            owner.PublishShare(messageBus, share, contract);
        }

        public void QueueResponse(Action queueResponse)
        {
            ThrowIfDisposed();
            owner.QueueResponse(queueResponse);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0, this);
        }
    }
}
