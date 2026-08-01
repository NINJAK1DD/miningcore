using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using NLog;

namespace Miningcore.Mining;

public interface IShareRecoveryFailureHandler
{
    Task StopClusterAsync(IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception databaseError, Exception journalError);
}

public sealed class ShareRecoveryFailureHandler : IShareRecoveryFailureHandler
{
    public ShareRecoveryFailureHandler(IProcessStatus processStatus,
        IHostApplicationLifetime applicationLifetime,
        Lazy<ICriticalNotificationSender> criticalNotificationSender,
        IShareRecoveryFatalState fatalState)
    {
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(criticalNotificationSender);
        ArgumentNullException.ThrowIfNull(fatalState);

        this.processStatus = processStatus;
        this.applicationLifetime = applicationLifetime;
        this.criticalNotificationSender = criticalNotificationSender;
        this.fatalState = fatalState;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IProcessStatus processStatus;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly Lazy<ICriticalNotificationSender> criticalNotificationSender;
    private readonly IShareRecoveryFatalState fatalState;
    private int failureSignalled;

    internal TimeSpan CriticalNotificationTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    public async Task StopClusterAsync(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception databaseError, Exception journalError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);

        if(Interlocked.Exchange(ref failureSignalled, 1) != 0)
            return;

        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);
        var pools = shares
            .Select(share => share.PoolId)
            .Where(poolId => !string.IsNullOrWhiteSpace(poolId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(poolId => poolId, StringComparer.Ordinal)
            .ToArray();
        var poolSummary = pools.Length > 0 ? string.Join(", ", pools) : "(unknown)";

        if(databaseError != null)
            logger.Fatal(databaseError,
                "PostgreSQL failed before the share-recovery journal fallback");

        if(journalError != null)
            logger.Fatal(journalError,
                "The share-recovery journal append or rollback failed");

        logger.Fatal(
            "Stopping cluster because neither PostgreSQL nor the recovery journal stored {0} share(s). Pools: {1}. Recovery file: {2}",
            shares.Count, poolSummary, absoluteRecoveryFilename);

        processStatus.MarkFailed(ProcessExitCodes.UnreconciledShareDurabilityLoss);

        try
        {
            fatalState.MarkFatal(shares.Count, pools, databaseError, journalError);
        }
        catch(Exception ex)
        {
            logger.Fatal(ex,
                "Unable to persist the share-recovery fatal-state marker {0}; exit status {1} still prevents automatic restart under the supplied systemd unit",
                fatalState.FatalStateFilename,
                ProcessExitCodes.UnreconciledShareDurabilityLoss);
        }

        var notification = new AdminNotification(
            "Fatal share-recovery fallback failure",
            $"Miningcore is stopping with exit status " +
            $"{ProcessExitCodes.UnreconciledShareDurabilityLoss} because neither PostgreSQL " +
            $"nor the recovery journal durably stored {shares.Count} share(s) for pool(s) " +
            $"{poolSummary}. Recovery file: {absoluteRecoveryFilename}. Fatal state: " +
            $"{fatalState.FatalStateFilename}. Preserve both files, investigate database and " +
            "storage health, reconcile the incident, and remove only the .fatal marker before " +
            "restarting.");

        try
        {
            using var timeout = new CancellationTokenSource(CriticalNotificationTimeout);
            await criticalNotificationSender.Value
                .SendCriticalAdminNotificationAsync(notification, timeout.Token)
                .WaitAsync(CriticalNotificationTimeout);
        }
        catch(Exception ex)
        {
            logger.Error(ex,
                "Critical share-recovery notification was not delivered within {0}; shutdown will continue",
                CriticalNotificationTimeout);
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}
