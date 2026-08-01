using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using NLog;

namespace Miningcore.Mining;

public interface ICandidatePersistenceFailureHandler
{
    void StopCluster(IReadOnlyCollection<Share> candidates, Exception databaseError,
        Exception journalError, bool journalSucceeded);
}

public sealed class CandidatePersistenceFailureHandler : ICandidatePersistenceFailureHandler
{
    public CandidatePersistenceFailureHandler(IProcessStatus processStatus,
        IHostApplicationLifetime applicationLifetime, IMessageBus messageBus,
        IShareRecoveryFatalState fatalState = null)
    {
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(messageBus);

        this.processStatus = processStatus;
        this.applicationLifetime = applicationLifetime;
        this.messageBus = messageBus;
        this.fatalState = fatalState;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IProcessStatus processStatus;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly IMessageBus messageBus;
    private readonly IShareRecoveryFatalState fatalState;
    private int failureSignalled;

    public void StopCluster(IReadOnlyCollection<Share> candidates,
        Exception databaseError, Exception journalError, bool journalSucceeded)
    {
        ArgumentNullException.ThrowIfNull(candidates);

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

        if(Interlocked.Exchange(ref failureSignalled, 1) != 0)
            return;

        var exitCode = journalSucceeded
            ? ProcessExitCodes.GeneralFailure
            : ProcessExitCodes.UnreconciledShareDurabilityLoss;

        if(!journalSucceeded && fatalState != null)
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
                    "Unable to persist block-candidate fatal-state marker; dedicated exit status still prevents automatic restart");
            }
        }

        try
        {
            messageBus.SendMessage(new AdminNotification(
                "Fatal block-candidate persistence failure",
                $"Miningcore is stopping with exit status {exitCode}. {durability} Candidates: {candidateDetails}"));
        }
        catch(Exception ex)
        {
            logger.Error(ex, "Unable to emit fatal candidate-persistence notification");
        }

        processStatus.MarkFailed(exitCode);
        applicationLifetime.StopApplication();
    }
}

internal sealed class NullCandidatePersistenceFailureHandler :
    ICandidatePersistenceFailureHandler
{
    public static readonly NullCandidatePersistenceFailureHandler Instance = new();

    public void StopCluster(IReadOnlyCollection<Share> candidates,
        Exception databaseError, Exception journalError, bool journalSucceeded)
    {
    }
}
