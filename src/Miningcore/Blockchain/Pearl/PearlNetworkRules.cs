using System.IO;

namespace Miningcore.Blockchain.Pearl;

internal enum PearlNetwork { Mainnet, Testnet, Testnet2, Regtest }

/// <summary>Height rules pinned to Pearl v1.5.5 chaincfg, independent of a daemon's claimed version.</summary>
internal static class PearlNetworkRules
{
    internal readonly record struct Rules(uint CertificateVersion, bool DenseOnly, bool RankPenalty);

    internal static string AddressPrefix(PearlNetwork network) => network switch
    {
        PearlNetwork.Mainnet => "prl",
        PearlNetwork.Testnet or PearlNetwork.Testnet2 => "tprl",
        PearlNetwork.Regtest => "rprl",
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };

    internal static Rules At(PearlNetwork network, long height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var (moe, dense, rank, salted) = network switch
        {
            PearlNetwork.Mainnet => (71935, 91630, 96251, 99000),
            PearlNetwork.Testnet => (1, 28262, 36761, 38648),
            PearlNetwork.Testnet2 => (54869, 80051, 80627, 83109),
            PearlNetwork.Regtest => (1, 0, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(network)),
        };
        return new Rules(height >= salted ? 3u : height >= moe ? 2u : 1u,
            dense != 0 && height >= dense, rank != 0 && height >= rank);
    }

    internal static void RequireCurrentMining(PearlNetwork network, long height, uint advertisedVersion)
    {
        var rules = At(network, height);
        // The pinned plain verifier applies current dense/rank restrictions even to old
        // headers. Historical serialization support must never imply historical mining.
        if(height == 0 || rules.CertificateVersion != 3 || !rules.RankPenalty ||
            advertisedVersion != rules.CertificateVersion)
            throw new InvalidDataException("Unsupported Pearl height or certificate version");
    }
}
