using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
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
    void PublishShare(Action publish);
    void QueueResponse(Action queueResponse);
}

internal interface IMiningAdmissionFailure
{
    Task HandleAfterAdmissionReleasedAsync();
}

public sealed class MiningFailStopCoordinator : IMiningFailStopCoordinator, IDisposable
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
        new(LockRecursionPolicy.SupportsRecursion);
    private int failStopRequested;

    public bool IsFailStopRequested =>
        Volatile.Read(ref failStopRequested) != 0;

    public CancellationToken Token => failStop.Token;

    public IMiningSubmissionAcceptance AcquireSubmissionAcceptance()
    {
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
        // ProcessStatus deliberately permits the non-restart durability-loss status to
        // upgrade an earlier general failure. Every invocation must reach it, including
        // failures discovered after shutdown has already started.
        processStatus.MarkFailed(exitCode);

        acceptanceGate.EnterWriteLock();

        try
        {
            if(Interlocked.Exchange(ref failStopRequested, 1) != 0)
                return false;

            // Cancellation happens while holding the same gate used by share publication and
            // response admission. Once this returns, no check-then-queue race can acknowledge
            // a share that did not first enter the accounting pipeline.
            failStop.Cancel();
        }
        finally
        {
            acceptanceGate.ExitWriteLock();
        }

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

        return true;
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
        deferred?.HandleAfterAdmissionReleasedAsync().GetAwaiter().GetResult();
        failure?.Throw();
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

        public void PublishShare(Action publish)
        {
            ThrowIfDisposed();
            owner.PublishShare(publish);
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
