namespace Miningcore.Stratum;

// Carries the admission decision across cleanup and concurrent pool recovery.
// Only expected local refusals use this marker; unrelated failures remain errors.
// Only the accept handler interprets it; subclasses may also reject submissions.
internal class StratumAdmissionClosedException(StratumError code, string message) :
    StratumException(code, message);
