using Microsoft.Extensions.Hosting;

namespace Miningcore;

public interface IProcessStatus
{
    int ExitCode { get; }
    void MarkFailed(int exitCode = ProcessExitCodes.GeneralFailure);
}

public static class ProcessExitCodes
{
    public const int GeneralFailure = 1;

    // EX_CONFIG: requires an operator to repair configuration or retained ownership.
    public const int ConfigurationRequiresOperator = 78;
    public const int PayoutOwnershipBlocked = ConfigurationRequiresOperator;

    // EX_IOERR. The packaged systemd unit deliberately does not restart this status because
    // Miningcore could not durably account for accepted shares in either PostgreSQL or the
    // recovery journal. An operator must reconcile the incident before starting it again.
    public const int UnreconciledShareDurabilityLoss = 74;
}

public sealed class ProcessStatus : IProcessStatus
{
    private int exitCode;

    public int ExitCode => Volatile.Read(ref exitCode);

    public void MarkFailed(int failureExitCode = ProcessExitCodes.GeneralFailure)
    {
        if(failureExitCode <= 0)
            throw new ArgumentOutOfRangeException(nameof(failureExitCode));

        // Preserve first failure within a priority, but never let ordinary failure
        // mask operator-required recovery. CAS retries prevent concurrent downgrades.
        while(true)
        {
            var current = Volatile.Read(ref exitCode);
            if(FailurePriority(failureExitCode) <= FailurePriority(current))
                return;
            if(Interlocked.CompareExchange(ref exitCode, failureExitCode, current) == current)
                return;
        }
    }

    private static int FailurePriority(int code) => code switch
    {
        ProcessExitCodes.UnreconciledShareDurabilityLoss => 3,
        ProcessExitCodes.ConfigurationRequiresOperator => 2,
        0 => 0,
        _ => 1,
    };
}

public abstract class ProcessStatusBackgroundService : BackgroundService
{
    protected ProcessStatusBackgroundService(IProcessStatus processStatus)
    {
        ArgumentNullException.ThrowIfNull(processStatus);
        ProcessStatus = processStatus;
    }

    protected IProcessStatus ProcessStatus { get; }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteCoreAsync(stoppingToken);
        }

        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
        {
            // Cancellation of the service execution token is the normal hosted-service stop
            // mechanism. A timeout or other exception escaping host shutdown is handled by Main.
            throw;
        }

        catch
        {
            ProcessStatus.MarkFailed();
            throw;
        }
    }

    protected abstract Task ExecuteCoreAsync(CancellationToken stoppingToken);
}
