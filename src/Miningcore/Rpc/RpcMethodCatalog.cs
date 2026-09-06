using System.Collections.Frozen;
using System.Reflection;

namespace Miningcore.Rpc;

internal static class RpcMethodCatalog
{
    // Only compile-time constants from these source-controlled protocol vocabularies.
    // Never discover methods from configuration, plugins, payloads or daemon responses.
    private static readonly FrozenSet<string> methods = Build();

    internal static string Label(string method) => method == null ? null :
        method.Length <= 64 && methods.Contains(method) ? method : "other";

    private static FrozenSet<string> Build()
    {
        Type[] protocols =
        {
            typeof(Blockchain.Bitcoin.BitcoinCommands),
            typeof(Blockchain.Cryptonote.CryptonoteCommands),
            typeof(Blockchain.Cryptonote.CryptonoteWalletCommands),
            typeof(Blockchain.Handshake.HandshakeWalletCommands),
            typeof(Blockchain.Zano.ZanoCommands), typeof(Blockchain.Zano.ZanoWalletCommands),
            typeof(Blockchain.Equihash.EquihashCommands), typeof(Blockchain.Equihash.VeruscoinCommands),
            typeof(Blockchain.Nexa.NexaCommands),
            typeof(Blockchain.Conceal.ConcealCommands), typeof(Blockchain.Conceal.ConcealWalletCommands),
            typeof(Blockchain.Xelis.XelisCommands), typeof(Blockchain.Xelis.XelisWalletCommands),
            typeof(Blockchain.Warthog.WarthogCommands),
            typeof(Blockchain.Beam.BeamExplorerCommands), typeof(Blockchain.Beam.BeamWalletCommands),
        };
        var result = protocols.SelectMany(Constants).ToHashSet(StringComparer.Ordinal);
        foreach(var method in Constants(typeof(Blockchain.Ethereum.EthCommands)))
        {
            if(method.StartsWith('_'))
            {
                result.Add("eth" + method);
                result.Add("ctxc" + method); // Reviewed built-in Cortex prefix, not arbitrary custom prefixes.
            }
            else result.Add(method);
        }

        // Built-in call sites that do not live in a public command vocabulary.
        result.UnionWith(new[] { "createauxblock", "submitauxblock", "getdeploymentinfo", "getblockheader", "getblockhash" });
        return result.ToFrozenSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> Constants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(x => x.IsLiteral && x.FieldType == typeof(string))
        .Select(x => (string) x.GetRawConstantValue());
}
