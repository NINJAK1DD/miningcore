using Microsoft.Extensions.Hosting;
using NLog;

namespace Miningcore.Mining;

public interface IMiningFailStopCoordinator
{
    bool IsFailStopRequested { get; }
    CancellationToken Token { get; }
    bool BeginFailStop(int exitCode);
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
    private int failStopRequested;

    public bool IsFailStopRequested =>
        Volatile.Read(ref failStopRequested) != 0;

    public CancellationToken Token => failStop.Token;

    public bool BeginFailStop(int exitCode)
    {
        if(Interlocked.Exchange(ref failStopRequested, 1) != 0)
            return false;

        // Close the accounting and response gates before beginning any slower incident work.
        // This prevents Miningcore from acknowledging or queueing more shares once it knows
        // that neither durable accounting destination is available.
        processStatus.MarkFailed(exitCode);
        failStop.Cancel();

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

    public void Dispose()
    {
        failStop.Dispose();
    }
}
