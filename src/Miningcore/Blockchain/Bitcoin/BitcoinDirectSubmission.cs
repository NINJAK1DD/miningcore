using NBitcoin;
using PersistedBlock = Miningcore.Persistence.Model.Block;
using PersistedBlockStatus = Miningcore.Persistence.Model.BlockStatus;

namespace Miningcore.Blockchain.Bitcoin;

public enum BitcoinDirectSubmissionOutcome
{
    ObservedActive,
    Ambiguous,
    DefinitiveMiss,
}

internal static class BitcoinDirectSubmission
{
    internal const string Prepared = "prepared";
    internal const string SubmittedUncertain = "submitted-uncertain";
    internal const string ObservedActive = "observed-active";
    internal const string Rejected = "rejected";
    internal const string LegacyObserved = "legacy-observed";
    internal const int MaximumSerializedBlockHexLength = 8_000_000;
    internal const int MinimumDefinitiveMisses = 3;
    internal static readonly TimeSpan UncertainLifetime = TimeSpan.FromMinutes(30);

    internal static bool RequiresReplay(string state) =>
        string.Equals(state, Prepared, StringComparison.Ordinal) ||
        string.Equals(state, SubmittedUncertain, StringComparison.Ordinal);

    internal static bool WasObserved(string state) =>
        string.Equals(state, ObservedActive, StringComparison.Ordinal) ||
        string.Equals(state, LegacyObserved, StringComparison.Ordinal);

    internal static void ValidatePreparedShare(Share share)
    {
        ArgumentNullException.ThrowIfNull(share);

        if(!string.Equals(share.DirectSubmissionState, Prepared,
               StringComparison.Ordinal) ||
           share.DirectSubmissionAttempts != 0 ||
           share.DirectSubmissionDefinitiveMisses != 0 ||
           share.DirectSubmissionLastAttempt.HasValue)
            throw new InvalidDataException(
                "A new direct submission outbox entry must be in the canonical prepared state");

        ValidatePayload(share.DirectSubmissionBlock, share.BlockHash,
            share.TransactionConfirmationData);
    }

    internal static void ValidatePersistedBlock(PersistedBlock block)
    {
        ValidatePersistedMetadata(block);

        if(!string.Equals(block.DirectSubmissionState, LegacyObserved,
               StringComparison.Ordinal))
            ValidatePayload(block.DirectSubmissionBlock, block.Hash,
                block.TransactionConfirmationData);
    }

    internal static void ValidatePersistedProjection(PersistedBlock block)
    {
        ValidatePersistedMetadata(block);

        if(RequiresReplay(block.DirectSubmissionState) ||
           !string.IsNullOrEmpty(block.DirectSubmissionBlock))
            ValidatePayload(block.DirectSubmissionBlock, block.Hash,
                block.TransactionConfirmationData);
    }

    private static void ValidatePersistedMetadata(PersistedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if(string.Equals(block.DirectSubmissionState, LegacyObserved,
               StringComparison.Ordinal))
        {
            if(!string.IsNullOrEmpty(block.DirectSubmissionBlock) ||
               block.DirectSubmissionAttempts != 0 ||
               block.DirectSubmissionDefinitiveMisses != 0 ||
               block.DirectSubmissionLastAttempt.HasValue)
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} has malformed legacy submission evidence");

            return;
        }

        if(block.DirectSubmissionState is not (Prepared or SubmittedUncertain or
               ObservedActive or Rejected) ||
           block.DirectSubmissionAttempts < 0 ||
           block.DirectSubmissionDefinitiveMisses < 0 ||
           block.DirectSubmissionDefinitiveMisses >
               block.DirectSubmissionAttempts)
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has malformed submission state");

        if(string.Equals(block.DirectSubmissionState, Prepared,
               StringComparison.Ordinal))
        {
            if(block.DirectSubmissionAttempts != 0 ||
               block.DirectSubmissionDefinitiveMisses != 0 ||
               block.DirectSubmissionLastAttempt.HasValue)
                throw new InvalidDataException(
                    $"Direct SOLO block {block.BlockHeight} has malformed prepared submission state");
        }
        else if(block.DirectSubmissionAttempts == 0 ||
                !block.DirectSubmissionLastAttempt.HasValue)
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has incomplete submission-attempt evidence");

        if((string.Equals(block.DirectSubmissionState, Prepared,
                StringComparison.Ordinal) ||
            string.Equals(block.DirectSubmissionState, SubmittedUncertain,
                StringComparison.Ordinal)) &&
           block.Status != PersistedBlockStatus.Pending)
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has a non-pending replayable submission state");
        if(string.Equals(block.DirectSubmissionState, Rejected,
               StringComparison.Ordinal) &&
           (block.Status != PersistedBlockStatus.Orphaned ||
            block.DirectSubmissionDefinitiveMisses < MinimumDefinitiveMisses))
            throw new InvalidDataException(
                $"Direct SOLO block {block.BlockHeight} has malformed rejected submission evidence");

    }

    internal static void ValidatePayload(string blockHex, string expectedHash,
        string expectedCoinbaseTxId)
    {
        if(string.IsNullOrWhiteSpace(blockHex) || blockHex.Length < 162 ||
           blockHex.Length > MaximumSerializedBlockHexLength ||
           blockHex.Length % 2 != 0 ||
           blockHex.Any(x => !(x is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException(
                "Direct submission payload must be canonical lowercase serialized-block hexadecimal");

        try
        {
            var block = NBitcoin.Block.Parse(blockHex, Network.Main);
            if(!string.Equals(block.GetHash().ToString(), expectedHash,
                   StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Direct submission payload hash differs from its candidate identity");
            if(block.Transactions.Count == 0 ||
               !string.Equals(block.Transactions[0].GetHash().ToString(),
                   expectedCoinbaseTxId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Direct submission payload coinbase differs from its settlement identity");
        }
        catch(InvalidDataException)
        {
            throw;
        }
        catch(Exception ex)
        {
            throw new InvalidDataException(
                "Direct submission payload is not a complete Bitcoin block", ex);
        }
    }
}
