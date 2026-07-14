using Microsoft.Extensions.Hosting;

namespace Miningcore;

public interface IProcessStatus
{
    int ExitCode { get; }
    void MarkFailed();
}

public sealed class ProcessStatus : IProcessStatus
{
    private int exitCode;

    public int ExitCode => Volatile.Read(ref exitCode);

    public void MarkFailed()
    {
        Interlocked.Exchange(ref exitCode, 1);
    }
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
