using Miningcore.Configuration;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Bitcoin.Configuration;

internal static class MergedMiningConfigLoader
{
    public static MergedMiningConfig GetNormalizedConfig(PoolConfig poolConfig)
    {
        ArgumentNullException.ThrowIfNull(poolConfig);

        var result = poolConfig.Extra
            .SafeExtensionDataAs<MergedMiningPoolConfigExtra>()
            ?.MergedMining;

        if(result == null)
            return null;

        result.AddressParameter = string.IsNullOrWhiteSpace(result.AddressParameter)
            ? "doge"
            : result.AddressParameter.Trim();

        if(result.AuxiliaryTemplatePollTimeoutMs <= 0)
            result.AuxiliaryTemplatePollTimeoutMs = 500;

        return result;
    }
}
