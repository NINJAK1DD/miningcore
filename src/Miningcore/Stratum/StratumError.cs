using Miningcore.Diagnostics;

namespace Miningcore.Stratum;

public enum StratumError
{
    Other = 20,
    JobNotFound = 21, // stale
    DuplicateShare = 22,
    LowDifficultyShare = 23,
    UnauthorizedWorker = 24,
    NotSubscribed = 25,
    MinusOne = -1
}

public class StratumException : Exception, IBoundedShareFailure
{
    public StratumException(StratumError code, string message) : base(message)
    {
        Code = code;
    }

    public StratumError Code { get; set; }

    int IBoundedShareFailure.DiagnosticCode => (int) Code;
    ShareFailureKind IBoundedShareFailure.DiagnosticKind => Code switch
    {
        StratumError.JobNotFound => ShareFailureKind.JobNotFound,
        StratumError.DuplicateShare => ShareFailureKind.DuplicateShare,
        StratumError.LowDifficultyShare => ShareFailureKind.LowDifficultyShare,
        StratumError.UnauthorizedWorker => ShareFailureKind.UnauthorizedWorker,
        StratumError.NotSubscribed => ShareFailureKind.NotSubscribed,
        _ => ShareFailureKind.Rejected,
    };
}
