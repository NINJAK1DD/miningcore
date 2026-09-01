using Miningcore.Blockchain.Bitcoin;
using Miningcore.Persistence.Model;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Mining;

/// <summary>
/// Persists financially significant block candidates before the submitting worker is acknowledged.
/// </summary>
public interface IBlockCandidateRecorder
{
    Task PersistBlockCandidateAsync(Share share);
    Task<DirectBlockSubmissionPreparation> PersistDirectBlockSubmissionAsync(
        Share share);
    Task CompleteDirectBlockSubmissionPreparationAsync(Share share,
        DirectBlockSubmissionPreparation preparation);
    Task<Block> RecordDirectBlockSubmissionAttemptAsync(Share share,
        BitcoinDirectSubmissionOutcome outcome, DateTime attemptedAt);
    Task<Block[]> GetDirectBlockSubmissionsForReplayAsync(string poolId,
        long afterId, int pageSize, CancellationToken ct);
    void BeginShutdown();
}

public enum DirectBlockSubmissionFailStopReason
{
    None,
    UnexpectedDatabaseFailure,
    CommittedCleanupFailure,
    CommitOutcomeUncertain,
}

public sealed record DirectBlockSubmissionPreparation(
    Exception DeferredFailStopError = null,
    DirectBlockSubmissionFailStopReason DeferredFailStopReason =
        DirectBlockSubmissionFailStopReason.UnexpectedDatabaseFailure,
    Exception JournalError = null);
