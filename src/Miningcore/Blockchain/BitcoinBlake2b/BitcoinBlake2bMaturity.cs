using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// Knots a7c4617524c334f542484bfef3919b9f12573840. Historical consensus,
// rolling consensus and wallet policy are different contracts. Never select
// reward maturity from the current wall clock, current tip MTP or active flag.
internal sealed record BitcoinBlake2bMaturity(int Start, int Enforce, int Release, int Maturity,
    int? RollingStart = null, int? RollingMaturity = null, long? Expiry = null)
{
    internal static readonly BitcoinBlake2bMaturity Mainnet = new(973440, 973440, 979920, 6480,
        979919, 25920, 1819756800);
    internal static readonly BitcoinBlake2bMaturity Regtest = new(int.MaxValue, int.MaxValue, int.MaxValue, 100);

    internal bool HistoricalActiveAt(long spendHeight) => (Expiry == null || this == Mainnet) &&
        spendHeight >= Enforce && spendHeight < Release;
    internal bool ActiveAt(long spendHeight) => HistoricalActiveAt(spendHeight) ||
        RollingStart.HasValue && spendHeight >= (this == Mainnet ? RollingStart.Value : Enforce);
    internal int WalletConfirmations => checked(Maturity + 1);

    internal int PolicyMaturity(uint rewardHeight, long parentMtp)
    {
        if(parentMtp < 0 || parentMtp > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(parentMtp));
        // Upstream checks expiry before the historical-height fallback.
        if(Expiry.HasValue && parentMtp >= Expiry.Value) return 100;
        return RollingStart.HasValue && rewardHeight >= RollingStart.Value ? RollingMaturity!.Value : Maturity;
    }

    internal int ConsensusMaturity(uint rewardHeight, long spendHeight, long parentMtp)
    {
        var historical = HistoricalActiveAt(spendHeight) && rewardHeight >= Start ? Maturity : 100;
        var rolling = RollingStart.HasValue && spendHeight >= (this == Mainnet ? RollingStart.Value : Enforce) &&
            rewardHeight >= RollingStart.Value && parentMtp < Expiry!.Value ? RollingMaturity!.Value : 100;
        return Math.Max(historical, rolling);
    }

    internal static BitcoinBlake2bMaturity ForNetwork(BitcoinBlake2bTemplate coin, string chain, string poolId)
    {
        if(chain is not ("main" or "regtest") || !coin.Networks.TryGetValue(chain, out var network))
            throw new PoolStartupException("Unsupported Bitcoin BLAKE2b maturity network", poolId);
        var values = new[] { network.Blake2bMaturityStart, network.Blake2bMaturityEnforce, network.Blake2bMaturityRelease };
        var expiry = network.Blake2bMaturityExpiry;
        if(values.All(x => x == null) && expiry == null) return chain == "main" ? Mainnet : Regtest;
        if(chain != "regtest" || values.Any(x => x == null) ||
            values.Any(x => x < 0 || x >= int.MaxValue) ||
            values[1] < 2 || values[2] <= values[1] || values[2] <= values[0] || values[2] - values[0] <= 100 ||
            expiry is <= 0 or > uint.MaxValue)
            throw new PoolStartupException("Long-maturity overrides require an explicit valid isolated-regtest schedule", poolId);
        return expiry.HasValue
            ? new(values[0]!.Value, values[1]!.Value, values[2]!.Value, 100,
                values[0]!.Value, values[2]!.Value - values[0]!.Value, expiry)
            : new(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[2]!.Value - values[0]!.Value);
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
        if(info?["height"]?.Type != JTokenType.Integer ||
            !long.TryParse(info["height"].ToString(), out var tip) || tip < 0 || tip >= int.MaxValue)
            throw new PoolStartupException("Bitcoin BLAKE2b deployment tip is invalid", poolId);
        var rolling = RollingStart.HasValue && (this != Mainnet || tip + 1 >= RollingStart.Value);
        var fields = new[] { "type", "height", rolling ? "coinbase_expiry_time" : "height_end", "coinbase_start_height", "maturity", "active" };
        if(deployment == null || deployment.Properties().Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)) ||
            deployment.Properties().Count() != fields.Length ||
            deployment["type"]?.Type != JTokenType.String || deployment["type"].Value<string>() != "flagday" ||
            !IntegerEquals(deployment["height"], rolling && this == Mainnet ? RollingStart!.Value : Enforce) ||
            !IntegerEquals(deployment[rolling ? "coinbase_expiry_time" : "height_end"], rolling ? Expiry!.Value : Release - 1) ||
            !IntegerEquals(deployment["coinbase_start_height"], rolling ? RollingStart!.Value : Start) ||
            !IntegerEquals(deployment["maturity"], rolling ? RollingMaturity!.Value : Maturity) ||
            deployment["active"]?.Type != JTokenType.Boolean || deployment["active"].Value<bool>() != ActiveAt(tip + 1))
            throw new PoolStartupException("Bitcoin BLAKE2b long-maturity deployment differs from the reviewed height/field contract", poolId);
    }

    private static bool IntegerEquals(JToken token, long expected) => token?.Type == JTokenType.Integer &&
        long.TryParse(token.ToString(), out var value) && value == expected;

    internal void ValidateRules(string[] rules, uint nextHeight, string poolId)
    {
        if(rules == null || rules.Any(x => x == null) ||
            rules.Count(x => x == "long_coinbase_maturity") != (ActiveAt(nextHeight) ? 1 : 0) ||
            rules.Any(x => x.StartsWith('!') && x is not ("!blake2b" or "!segwit")))
            throw new PoolStartupException("Bitcoin BLAKE2b GBT rules contradict the reviewed next-height contract", poolId);
    }

    internal int RequiredConfirmations(int operatorConfirmations, int? policyMaturity = null) =>
        Math.Max(checked((policyMaturity ?? Maturity) + 1), operatorConfirmations);
    internal int Remaining(int confirmations, int operatorConfirmations, int? policyMaturity = null) =>
        (int) Math.Max(0L, (long) RequiredConfirmations(operatorConfirmations, policyMaturity) - confirmations);
    internal double Progress(int confirmations, int operatorConfirmations, bool walletImmature, int? policyMaturity = null) =>
        Math.Clamp((double) confirmations / RequiredConfirmations(operatorConfirmations, policyMaturity), 0,
            walletImmature ? Math.BitDecrement(1.0) : 1.0);
}
