using Miningcore.Configuration;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// Reviewed Knots 8c85b1585dac23f964e2dd32045624de7f02aa58:
// src/kernel/chainparams.cpp and src/consensus/params.h. The catalogue repeats
// these values as operator-readable JSON; loader checks and tests pin it here.
internal static class BitcoinBlake2bConsensus
{
    internal const uint MainnetActivationHeight = 961640;
    internal const byte MainnetTargetShift = 22;
    internal const string MainnetActivationHeadline = "8-30 NYPost Deride And Conquer";
    internal const byte RegtestTargetShift = 20;

    internal static bool MatchesMainnet(BitcoinTemplate.BitcoinNetworkParams network) =>
        network?.Blake2bActivationHeight == MainnetActivationHeight &&
        network.Blake2bTargetShift == MainnetTargetShift &&
        string.Equals(network.Blake2bActivationHeadline, MainnetActivationHeadline,
            StringComparison.Ordinal);
}
