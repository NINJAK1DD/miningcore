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
    private int failureSignalled;

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
            $"hash={candidate.BlockHash ?? "(none)"}, type={candidate.BlockType ?? "(none)"}, " +
            $"marker={candidate.TransactionConfirmationData ?? "(none)"}"));
        var durability = journalSucceeded
            ? "The candidate was written to the recovery journal, but the unexpected database failure makes the live persistence pipeline unsafe."
            : "Neither PostgreSQL nor the recovery journal durably stored the candidate.";
        var failure = journalError ?? databaseError ??
            new IOException("Unknown candidate-persistence failure");

        if(databaseError != null)
            logger.Fatal(databaseError,
                "PostgreSQL failed while persisting a block candidate");

        if(journalError != null)
            logger.Fatal(journalError,
                "The recovery journal failed while persisting a block candidate");

        logger.Fatal(failure,
            "Stopping cluster after financially significant block-candidate persistence failure. {0} Candidates: {1}. Database error: {2}. Journal error: {3}",
            durability, candidateDetails, databaseError?.Message ?? "(none)",
            journalError?.Message ?? "(none)");

        if(!journalSucceeded)
        {
            try
            {
                var pools = candidates.Select(x => x.PoolId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                fatalState.MarkFatal(candidates.Count, pools, databaseError,
                    journalError);
            }
            catch(Exception ex)
            {
                logger.Fatal(ex,
                    "Unable to persist block-candidate fatal-state marker {0}; exit status {1} remains active",
                    fatalState.FatalStateFilename, exitCode);
            }
        }

        // The status and fatal latch above are safety-critical for every incident. Suppress only
        // duplicate operator notifications once shutdown has already been announced.
        if(Interlocked.Exchange(ref failureSignalled, 1) != 0)
            return;

        var notification = new AdminNotification(
            "Fatal block-candidate persistence failure",
            $"Miningcore is stopping with exit status {exitCode}. {durability} " +
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
            logger.Error(ex,
                "Critical block-candidate notification was not delivered within {0}; shutdown will continue",
                CriticalNotificationTimeout);
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
