using Miningcore.Stratum;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Pearl;

internal static class PearlWorkerIdentity
{
    internal static (string Miner, string Worker) Parse(string wallet, JToken separateWorker, PearlNetwork network)
    {
        if(wallet == null || wallet.Length > 160 || separateWorker != null && separateWorker.Type != JTokenType.String)
            throw new StratumException(StratumError.UnauthorizedWorker, "Invalid Pearl worker identity");

        var separator = wallet.IndexOf('.');
        var address = separator < 0 ? wallet : wallet[..separator];
        PearlAddress.RewardScript(address, PearlNetworkRules.AddressPrefix(network));
        var suffix = separator < 0 ? null : wallet[(separator + 1)..];
        var explicitName = separateWorker?.Value<string>();
        // SRBMiner uses wallet.worker; BzMiner v100.60 sends a separate worker.
        // Never silently attribute a conflicting identity to either account.
        if(suffix != null && explicitName != null && suffix != explicitName)
            throw new StratumException(StratumError.UnauthorizedWorker, "Conflicting Pearl worker names");
        var name = suffix ?? explicitName ?? string.Empty;
        if(name.Length > 64 || name.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '-' or '_')))
            throw new StratumException(StratumError.UnauthorizedWorker, "Invalid Pearl worker name");
        return (address.ToLowerInvariant(), name);
    }
}
