using Miningcore.Blockchain;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using NLog;

namespace Miningcore.Mining;

public interface IShareRecoveryFailureHandler
{
    Task StopClusterAsync(IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception databaseError, Exception journalError);
    Task StopClusterAfterJournalAsync(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception pipelineError);
    Task StopClusterForUncertainCommitAsync(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception commitError);
}

public sealed class ShareRecoveryFailureHandler : IShareRecoveryFailureHandler
{
    public ShareRecoveryFailureHandler(IMiningFailStopCoordinator failStopCoordinator,
        Lazy<ICriticalNotificationSender> criticalNotificationSender,
        IShareRecoveryFatalState fatalState)
    {
        ArgumentNullException.ThrowIfNull(failStopCoordinator);
        ArgumentNullException.ThrowIfNull(criticalNotificationSender);
        ArgumentNullException.ThrowIfNull(fatalState);

        this.failStopCoordinator = failStopCoordinator;
        this.criticalNotificationSender = criticalNotificationSender;
        this.fatalState = fatalState;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IMiningFailStopCoordinator failStopCoordinator;
    private readonly Lazy<ICriticalNotificationSender> criticalNotificationSender;
    private readonly IShareRecoveryFatalState fatalState;
    private int notifiedSeverity;

    internal TimeSpan CriticalNotificationTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    public async Task StopClusterAsync(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception databaseError, Exception journalError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);

        failStopCoordinator.BeginFailStop(
            ProcessExitCodes.UnreconciledShareDurabilityLoss);

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

        try
        {
            fatalState.MarkFatalShares(shares, databaseError, journalError);
        }
        catch(Exception ex)
        {
            logger.Fatal(ex,
                "Unable to persist the share-recovery fatal-state marker {0}; exit status {1} still prevents automatic restart under the supplied systemd unit",
                fatalState.FatalStateFilename,
                ProcessExitCodes.UnreconciledShareDurabilityLoss);
        }

        // Always refresh the fail-closed state above. Only the slower operator alert is
        // de-duplicated after the first incident.
        if(!TryClaimNotificationSeverity(2))
            return;

        var notification = new AdminNotification(
            "Fatal share-recovery fallback failure",
            $"Miningcore is stopping with exit status " +
            $"{ProcessExitCodes.UnreconciledShareDurabilityLoss} because neither PostgreSQL " +
            $"nor the recovery journal durably stored {shares.Count} share(s) for pool(s) " +
            $"{poolSummary}. Recovery file: {absoluteRecoveryFilename}. Fatal state: " +
            $"{fatalState.FatalStateFilename}. Preserve both files, investigate database and " +
            "storage health, reconcile the incident, and remove only that exact fatal-state file before " +
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
    }

    public async Task StopClusterAfterJournalAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception pipelineError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        failStopCoordinator.BeginFailStop(ProcessExitCodes.GeneralFailure);
        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);

        logger.Fatal(pipelineError,
            "Stopping cluster after an unexpected share-persistence pipeline failure. " +
            "The complete unresolved set of {0} share(s) was force-flushed to {1}.",
            shares.Count, absoluteRecoveryFilename);

        if(!TryClaimNotificationSeverity(1))
            return;

        var notification = new AdminNotification(
            "Share persistence pipeline stopped",
            $"Miningcore is stopping with exit status {ProcessExitCodes.GeneralFailure} after " +
            $"an unexpected accounting-pipeline failure. All {shares.Count} unresolved share(s) " +
            $"were force-flushed to {absoluteRecoveryFilename}. Import and verify that journal " +
            "before resuming normal operation.");
        await SendCriticalNotificationSafelyAsync(notification);
    }

    public async Task StopClusterForUncertainCommitAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception commitError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        failStopCoordinator.BeginFailStop(
            ProcessExitCodes.UnreconciledShareDurabilityLoss);
        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);
        var suppression = new InvalidOperationException(
            "Recovery-journal replay was intentionally suppressed because the PostgreSQL commit outcome is uncertain");

        logger.Fatal(commitError,
            "Stopping cluster because the PostgreSQL commit outcome is uncertain for {0} " +
            "share(s). They were not copied to the importable recovery journal to avoid duplicate accounting.",
            shares.Count);

        try
        {
            fatalState.MarkFatalShares(shares, commitError, suppression);
        }
        catch(Exception ex)
        {
            logger.Fatal(ex,
                "Unable to persist uncertain-commit fatal state {0}; exit status {1} remains active",
                fatalState.FatalStateFilename,
                ProcessExitCodes.UnreconciledShareDurabilityLoss);
        }

        if(!TryClaimNotificationSeverity(2))
            return;

        var notification = new AdminNotification(
            "Uncertain PostgreSQL share commit",
            $"Miningcore is stopping with exit status " +
            $"{ProcessExitCodes.UnreconciledShareDurabilityLoss} because PostgreSQL may or may " +
            $"not have committed {shares.Count} share(s). The importable journal was deliberately " +
            $"not extended. Exact share records are in {fatalState.FatalStateFilename}. Reconcile " +
            $"them against PostgreSQL before removing that marker. Recovery file: " +
            $"{absoluteRecoveryFilename}.");
        await SendCriticalNotificationSafelyAsync(notification);
    }

    private bool TryClaimNotificationSeverity(int severity)
    {
        while(true)
        {
            var previous = Volatile.Read(ref notifiedSeverity);
            if(previous >= severity)
                return false;
            if(Interlocked.CompareExchange(ref notifiedSeverity, severity,
                   previous) == previous)
                return true;
        }
    }

    private async Task SendCriticalNotificationSafelyAsync(
        AdminNotification notification)
    {
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
    }
}
