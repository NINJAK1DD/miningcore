using Miningcore.Rpc;
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
    Task StopClusterAfterJournalAsync(IReadOnlyCollection<Share> recoverableShares,
        string recoveryFilename, IReadOnlyCollection<Share> quarantinedShares,
        string quarantineFilename, Exception pipelineError);
    Task StopClusterAfterCommittedCleanupAsync(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception cleanupError);
    Task StopClusterForUncertainCommitAsync(IReadOnlyCollection<Share> shares,
        string recoveryFilename, Exception commitError);
    Task StopClusterAfterReplaySafeCommittedCleanupAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception cleanupError, Exception journalError);
    Task StopClusterForReplaySafeUncertainCommitAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception commitError);
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
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterAsync", failure: databaseError);

        if(journalError != null)
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterAsync", failure: journalError);

        logger.Fatal(
            "Stopping cluster because neither PostgreSQL nor the recovery journal stored {0} share(s). Pools: {1}. Recovery file: {2}",
            shares.Count, poolSummary, absoluteRecoveryFilename);

        try
        {
            fatalState.MarkFatalShares(shares, databaseError, journalError);
        }
        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterAsync", failure: ex);
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
            $"storage health. {ShareRecoveryFatalState.OperatorAcknowledgementInstruction}");

        try
        {
            using var timeout = new CancellationTokenSource(CriticalNotificationTimeout);
            await criticalNotificationSender.Value
                .SendCriticalAdminNotificationAsync(notification, timeout.Token)
                .WaitAsync(CriticalNotificationTimeout);
        }
        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "ShareRecoveryFailureHandler.StopClusterAsync", failure: ex);
        }
    }

    public async Task StopClusterAfterJournalAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception pipelineError)
    {
        await StopClusterAfterJournalAsync(shares, recoveryFilename,
            Array.Empty<Share>(), null, pipelineError);
    }

    public async Task StopClusterAfterJournalAsync(
        IReadOnlyCollection<Share> recoverableShares, string recoveryFilename,
        IReadOnlyCollection<Share> quarantinedShares, string quarantineFilename,
        Exception pipelineError)
    {
        ArgumentNullException.ThrowIfNull(recoverableShares);
        ArgumentNullException.ThrowIfNull(quarantinedShares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        if(quarantinedShares.Count > 0)
            ArgumentException.ThrowIfNullOrWhiteSpace(quarantineFilename);
        failStopCoordinator.BeginFailStop(ProcessExitCodes.GeneralFailure);
        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);
        var absoluteQuarantineFilename = quarantinedShares.Count > 0
            ? Path.GetFullPath(quarantineFilename)
            : null;

        var recoverableSummary = recoverableShares.Count > 0
            ? $"{recoverableShares.Count} recoverable share(s) were force-flushed to " +
              $"{absoluteRecoveryFilename}"
            : "No importable recovery-journal records remained";
        var quarantineSummary = quarantinedShares.Count > 0
            ? $" {quarantinedShares.Count} rejected share(s) were written to quarantine " +
              $"{absoluteQuarantineFilename}; that file must not be imported with -rs and " +
              "requires manual financial reconciliation."
            : string.Empty;

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterAfterJournalAsync", failure: pipelineError);
        logger.Fatal("Stopping cluster after share-persistence failure. {0}.{1}", recoverableSummary, quarantineSummary);

        if(!TryClaimNotificationSeverity(1))
            return;

        var notification = new AdminNotification(
            "Share persistence pipeline stopped",
            $"Miningcore is stopping with exit status {ProcessExitCodes.GeneralFailure} after " +
            $"an unexpected accounting-pipeline failure. {recoverableSummary}." +
            quarantineSummary + (recoverableShares.Count > 0
                ? " Import and verify only the recovery journal before resuming normal operation."
                : " Do not run -rs against a quarantine file."));
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

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterForUncertainCommitAsync", failure: commitError);

        try
        {
            fatalState.MarkFatalShares(shares, commitError, suppression,
                "postgresql-commit-outcome-uncertain");
        }
        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterForUncertainCommitAsync", failure: ex);
        }

        if(!TryClaimNotificationSeverity(2))
            return;

        var notification = new AdminNotification(
            "Uncertain PostgreSQL share commit",
            $"Miningcore is stopping with exit status " +
            $"{ProcessExitCodes.UnreconciledShareDurabilityLoss} because PostgreSQL may or may " +
            $"not have committed {shares.Count} share(s). The importable journal was deliberately " +
            $"not extended. Exact share records are in the detail sidecar referenced by " +
            $"{fatalState.FatalStateFilename}. Recovery file: {absoluteRecoveryFilename}. " +
            ShareRecoveryFatalState.OperatorAcknowledgementInstruction);
        await SendCriticalNotificationSafelyAsync(notification);
    }

    public async Task StopClusterAfterCommittedCleanupAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception cleanupError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        failStopCoordinator.BeginFailStop(ProcessExitCodes.GeneralFailure);
        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterAfterCommittedCleanupAsync", failure: cleanupError);

        if(!TryClaimNotificationSeverity(1))
            return;

        var notification = new AdminNotification(
            "Share transaction cleanup failed after commit",
            $"Miningcore is stopping with exit status {ProcessExitCodes.GeneralFailure} after " +
            $"PostgreSQL committed {shares.Count} share(s), but transaction or connection " +
            "cleanup failed. Those committed records were deliberately excluded from the " +
            $"replayable recovery journal at {absoluteRecoveryFilename}. Investigate the " +
            "database provider and connection health before restarting.");
        await SendCriticalNotificationSafelyAsync(notification);
    }

    public async Task StopClusterAfterReplaySafeCommittedCleanupAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception cleanupError, Exception journalError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        failStopCoordinator.BeginFailStop(ProcessExitCodes.GeneralFailure);
        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterAfterReplaySafeCommittedCleanupAsync", failure: cleanupError);
        if(journalError != null)
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "ShareRecoveryFailureHandler.StopClusterAfterReplaySafeCommittedCleanupAsync", failure: journalError);

        if(!TryClaimNotificationSeverity(1))
            return;

        var journalSummary = journalError == null
            ? $" A replay-safe duplicate was also appended to {absoluteRecoveryFilename}."
            : " The recovery-journal duplicate could not be appended, but PostgreSQL committed the authoritative outbox row.";
        var notification = new AdminNotification(
            "Direct block transaction cleanup failed after commit",
            $"Miningcore propagated {shares.Count} direct block submission(s), then requested " +
            $"exit status {ProcessExitCodes.GeneralFailure} because transaction or connection " +
            "cleanup failed after PostgreSQL committed the durable outbox row." +
            journalSummary + " Investigate database-provider and connection health before restarting.");
        await SendCriticalNotificationSafelyAsync(notification);
    }

    public async Task StopClusterForReplaySafeUncertainCommitAsync(
        IReadOnlyCollection<Share> shares, string recoveryFilename,
        Exception commitError)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilename);
        failStopCoordinator.BeginFailStop(
            ProcessExitCodes.UnreconciledShareDurabilityLoss);
        var absoluteRecoveryFilename = Path.GetFullPath(recoveryFilename);

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterForReplaySafeUncertainCommitAsync", failure: commitError);

        try
        {
            fatalState.MarkFatalShares(shares, commitError, null,
                "bitcoin-direct-postgresql-commit-outcome-uncertain");
        }
        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "ShareRecoveryFailureHandler.StopClusterForReplaySafeUncertainCommitAsync", failure: ex);
        }

        if(!TryClaimNotificationSeverity(2))
            return;

        var notification = new AdminNotification(
            "Uncertain PostgreSQL direct-block commit",
            $"Miningcore propagated {shares.Count} direct block submission(s), then requested " +
            $"exit status {ProcessExitCodes.UnreconciledShareDurabilityLoss} because PostgreSQL " +
            "may or may not have committed their outbox rows. Exact replay-safe records are in " +
            $"{absoluteRecoveryFilename}; their stable unique identities make recovery import " +
            "idempotent. Reconcile PostgreSQL and the journal before resuming mining. " +
            ShareRecoveryFatalState.OperatorAcknowledgementInstruction);
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
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "ShareRecoveryFailureHandler.SendCriticalNotificationSafelyAsync", failure: ex);
        }
    }
}
