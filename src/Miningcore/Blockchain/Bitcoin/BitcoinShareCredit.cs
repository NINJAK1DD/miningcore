namespace Miningcore.Blockchain.Bitcoin;

internal static class BitcoinShareCredit
{
    // Candidate admission samples work at the easiest qualifying chain target.
    // Cap even an unusually strong candidate: proof-dependent or exception-only
    // caps would reward selective submission above the assignment target.
    internal static double ForCandidate(double assignedCredit, double candidateDifficulty)
    {
        if(!double.IsFinite(assignedCredit) || assignedCredit <= 0 ||
            !double.IsFinite(candidateDifficulty) || candidateDifficulty <= 0)
            throw new InvalidDataException("Candidate credit requires finite positive issued and chain difficulty");
        return Math.Min(assignedCredit, candidateDifficulty);
    }
}
