using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// Knots 58398baf33e588779685ead478e6397bb28ed3d6. RPC height_end is
// inclusive; the source release height is exclusive. Wallet/mempool policy
// applies Maturity to ALL coinbases and survives consensus deactivation.
internal sealed record BitcoinBlake2bMaturity(int Start, int Enforce, int Release, int Maturity)
{
    internal static readonly BitcoinBlake2bMaturity Mainnet = new(973440, 973440, 979920, 6480);
    internal static readonly BitcoinBlake2bMaturity Regtest = new(int.MaxValue, int.MaxValue, int.MaxValue, 100);

    internal bool ActiveAt(long spendHeight) => spendHeight >= Enforce && spendHeight < Release;
    internal int ConsensusDepth(long coinbaseHeight, long spendHeight) =>
        ActiveAt(spendHeight) && coinbaseHeight >= Start ? Maturity : 100;
    internal int WalletConfirmations => checked(Maturity + 1);

    internal static BitcoinBlake2bMaturity ForNetwork(BitcoinBlake2bTemplate coin, string chain, string poolId)
    {
        if(chain is not ("main" or "regtest") || !coin.Networks.TryGetValue(chain, out var network))
            throw new PoolStartupException("Unsupported Bitcoin BLAKE2b maturity network", poolId);
        var values = new[] { network.Blake2bMaturityStart, network.Blake2bMaturityEnforce, network.Blake2bMaturityRelease };
        if(values.All(x => x == null)) return chain == "main" ? Mainnet : Regtest;
        if(chain != "regtest" || values.Any(x => x == null) ||
            values.Any(x => x < 0 || x >= int.MaxValue) ||
            values[1] < values[0] || values[2] <= values[1] || values[2] - values[0] <= 100)
            throw new PoolStartupException("Long-maturity overrides require an explicit valid isolated-regtest schedule", poolId);
        return new(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[2]!.Value - values[0]!.Value);
    }

    internal void ValidateDeployment(JObject info, string poolId)
    {
        var deployment = (info?["deployments"] as JObject)?["long_coinbase_maturity"] as JObject;
        // The reviewed unscheduled regtest defaults intentionally omit this
        // deployment. This exception is selected by the explicit regtest
        // contract only; mainnet and scheduled fixtures require every field.
        if(this == Regtest && info?["deployments"] is JObject deployments &&
            deployments["long_coinbase_maturity"] == null &&
            info["height"]?.Type == JTokenType.Integer &&
            long.TryParse(info["height"].ToString(), out var regtestTip) && regtestTip >= 0 && regtestTip < int.MaxValue)
            return;
        var fields = new[] { "type", "height", "height_end", "coinbase_start_height", "maturity", "active" };
        if(deployment == null || deployment.Properties().Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)) ||
            deployment.Properties().Count() != fields.Length ||
            deployment["type"]?.Type != JTokenType.String || deployment["type"].Value<string>() != "flagday" ||
            !IntegerEquals(deployment["height"], Enforce) ||
            !IntegerEquals(deployment["height_end"], Release - 1) ||
            !IntegerEquals(deployment["coinbase_start_height"], Start) ||
            !IntegerEquals(deployment["maturity"], Maturity) ||
            info?["height"]?.Type != JTokenType.Integer ||
            !long.TryParse(info["height"].ToString(), out var tip) || tip < 0 || tip >= int.MaxValue ||
            deployment["active"]?.Type != JTokenType.Boolean || deployment["active"].Value<bool>() != ActiveAt(tip + 1))
            throw new PoolStartupException("Bitcoin BLAKE2b long-maturity deployment differs from the reviewed height/field contract", poolId);
    }

    private static bool IntegerEquals(JToken token, int expected) => token?.Type == JTokenType.Integer &&
        long.TryParse(token.ToString(), out var value) && value == expected;

    internal void ValidateRules(string[] rules, uint nextHeight, string poolId)
    {
        if(rules == null || rules.Any(x => x == null) ||
            rules.Count(x => x == "long_coinbase_maturity") != (ActiveAt(nextHeight) ? 1 : 0) ||
            rules.Any(x => x.StartsWith('!') && x is not ("!blake2b" or "!segwit")))
            throw new PoolStartupException("Bitcoin BLAKE2b GBT rules contradict the reviewed next-height contract", poolId);
    }

    internal int RequiredConfirmations(int operatorConfirmations) => Math.Max(WalletConfirmations, operatorConfirmations);
    internal int Remaining(int confirmations, int operatorConfirmations) =>
        (int) Math.Max(0L, (long) RequiredConfirmations(operatorConfirmations) - confirmations);
    internal double Progress(int confirmations, int operatorConfirmations, bool walletImmature) =>
        Math.Clamp((double) confirmations / RequiredConfirmations(operatorConfirmations), 0,
            walletImmature ? Math.BitDecrement(1.0) : 1.0);
}
