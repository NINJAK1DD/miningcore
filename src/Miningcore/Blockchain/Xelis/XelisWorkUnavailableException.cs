using Miningcore.Stratum;

namespace Miningcore.Blockchain.Xelis;

// A pool-side consensus transition is not evidence of miner misbehavior.
internal sealed class XelisWorkUnavailableException() :
    StratumAdmissionClosedException(StratumError.JobNotFound, "mining work unavailable");
