namespace Miningcore.Diagnostics;

// Only bounded metadata crosses this boundary. A string label would let an
// implementation accidentally return a credential-bearing exception message.
internal interface IBoundedShareFailure
{
    ShareFailureKind DiagnosticKind { get; }
    int DiagnosticCode { get; }
}

internal enum ShareFailureKind
{
    Rejected,
    JobNotFound,
    DuplicateShare,
    LowDifficultyShare,
    UnauthorizedWorker,
    NotSubscribed,
    InvalidJobChainIndex,
    InvalidWorker,
    InvalidNonce,
    InvalidBlockChainIndex,
}
