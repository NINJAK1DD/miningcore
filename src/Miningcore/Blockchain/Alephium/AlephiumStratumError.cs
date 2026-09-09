using Miningcore.Diagnostics;

namespace Miningcore.Blockchain.Alephium;

public enum AlephiumStratumError
{
    JobNotFound = 20,
    InvalidJobChainIndex = 21,
    InvalidWorker = 22,
    InvalidNonce = 23,
    DuplicatedShare = 24,
    LowDifficultyShare = 25,
    InvalidBlockChainIndex = 26,
    MinusOne = -1
}

public class AlephiumStratumException : Exception, IBoundedShareFailure
{
    public AlephiumStratumException(AlephiumStratumError code, string message) : base(message)
    {
        Code = code;
    }

    public AlephiumStratumError Code { get; set; }

    int IBoundedShareFailure.DiagnosticCode => (int) Code;
    ShareFailureKind IBoundedShareFailure.DiagnosticKind => Code switch
    {
        AlephiumStratumError.JobNotFound => ShareFailureKind.JobNotFound,
        AlephiumStratumError.InvalidJobChainIndex => ShareFailureKind.InvalidJobChainIndex,
        AlephiumStratumError.InvalidWorker => ShareFailureKind.InvalidWorker,
        AlephiumStratumError.InvalidNonce => ShareFailureKind.InvalidNonce,
        AlephiumStratumError.DuplicatedShare => ShareFailureKind.DuplicateShare,
        AlephiumStratumError.LowDifficultyShare => ShareFailureKind.LowDifficultyShare,
        AlephiumStratumError.InvalidBlockChainIndex => ShareFailureKind.InvalidBlockChainIndex,
        _ => ShareFailureKind.Rejected,
    };
}
