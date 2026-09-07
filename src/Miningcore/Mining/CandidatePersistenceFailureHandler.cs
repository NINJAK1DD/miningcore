using Miningcore.Rpc;
using Miningcore.Blockchain;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using NLog;

namespace Miningcore.Mining;

public interface ICandidatePersistenceFailureHandler
{
    Task StopClusterAsync(IReadOnlyCollection<Share> candidates,
        Exception databaseError, Exception journalError, bool journalSucceeded);
}

public sealed class CandidatePersistenceFailureHandler :
    ICandidatePersistenceFailureHandler
{
    public CandidatePersistenceFailureHandler(
        IMiningFailStopCoordinator failStopCoordinator,
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

    public async Task StopClusterAsync(IReadOnlyCollection<Share> candidates,
        Exception databaseError, Exception journalError, bool journalSucceeded)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var exitCode = journalSucceeded
            ? ProcessExitCodes.GeneralFailure
            : ProcessExitCodes.UnreconciledShareDurabilityLoss;

        // Every invocation reaches the coordinator so a later dual-target failure can upgrade
        // an earlier general shutdown to the non-restart durability-loss status.
        failStopCoordinator.BeginFailStop(exitCode);

        var candidateDetails = string.Join("; ", candidates.Select(candidate =>
            $"pool={candidate.PoolId}, miner={candidate.Miner}, height={candidate.BlockHeight}, " +
            $"type={candidate.BlockType ?? "(none)"}"));
        var durability = journalSucceeded
            ? "The candidate was written to the recovery journal, but the unexpected database failure makes the live persistence pipeline unsafe."
            : "Neither PostgreSQL nor the recovery journal durably stored the candidate.";
        var failure = journalError ?? databaseError ??
            new IOException("Unknown candidate-persistence failure");

        if(databaseError != null)
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "CandidatePersistenceFailureHandler.StopClusterAsync", failure: databaseError);

        if(journalError != null)
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "CandidatePersistenceFailureHandler.StopClusterAsync", failure: journalError);

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "CandidatePersistenceFailureHandler.StopClusterAsync", failure: failure);

        logger.Fatal("Stopping cluster with exit status {0} after block-candidate persistence failure. {1} Candidate count: {2}. Preserve PostgreSQL and recovery evidence.",
            exitCode, durability, candidates.Count);

        if(!journalSucceeded)
        {
            try
            {
                fatalState.MarkFatalShares(candidates, databaseError,
                    journalError);
            }
            catch(Exception ex)
            {
                RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "CandidatePersistenceFailureHandler.StopClusterAsync", failure: ex);
            }
        }

        // The status and fatal latch above are safety-critical for every incident. Alerts are
        // de-duplicated by severity so a later dual-target loss always upgrades the earlier
        // journalled-candidate notification seen by the operator.
        var severity = journalSucceeded ? 1 : 2;
        if(!TryClaimNotificationSeverity(severity, out var previousSeverity))
            return;

        var escalated = severity == 2 && previousSeverity > 0;
        var notification = new AdminNotification(
            escalated
                ? "Escalated block-candidate durability loss"
                : "Fatal block-candidate persistence failure",
            $"Miningcore is stopping with exit status {exitCode}. " +
            (escalated
                ? "This escalates an earlier journalled-candidate shutdown: "
                : string.Empty) +
            $"{durability} " +
            $"Candidates: {candidateDetails} Fatal state: " +
            $"{(journalSucceeded ? "(not required; journal succeeded)" : fatalState.FatalStateFilename)}");

        try
        {
            using var timeout = new CancellationTokenSource(
                CriticalNotificationTimeout);
            await criticalNotificationSender.Value
                .SendCriticalAdminNotificationAsync(notification, timeout.Token)
                .WaitAsync(CriticalNotificationTimeout);
        }
        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "CandidatePersistenceFailureHandler.StopClusterAsync", failure: ex);
        }
    }

    private bool TryClaimNotificationSeverity(int severity,
        out int previousSeverity)
    {
        while(true)
        {
            previousSeverity = Volatile.Read(ref notifiedSeverity);

            if(previousSeverity >= severity)
                return false;

            if(Interlocked.CompareExchange(ref notifiedSeverity, severity,
                   previousSeverity) == previousSeverity)
                return true;
        }
    }
}

internal sealed class NullCandidatePersistenceFailureHandler :
    ICandidatePersistenceFailureHandler
{
    public static readonly NullCandidatePersistenceFailureHandler Instance = new();

    public Task StopClusterAsync(IReadOnlyCollection<Share> candidates,
        Exception databaseError, Exception journalError, bool journalSucceeded) =>
        Task.CompletedTask;
}
