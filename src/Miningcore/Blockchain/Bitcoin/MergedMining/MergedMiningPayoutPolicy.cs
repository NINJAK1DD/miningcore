using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

/// <summary>One configuration contract for deployment preflight and job-manager startup.</summary>
internal static class MergedMiningPayoutPolicy
{
    internal static PoolConfig Validate(PoolConfig parent, ClusterConfig cluster,
        bool requireAssignedTemplates)
    {
        var merged = MergedMiningConfigLoader.GetNormalizedConfig(parent);
        if(merged?.Enabled != true)
            return null;

        EnsurePaymentProcessing(parent, "parent");
        if(string.IsNullOrWhiteSpace(merged.AuxPoolId))
            throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': mergedMining.auxPoolId is required", parent.Id);

        var auxiliary = cluster.Pools.FirstOrDefault(pool =>
            string.Equals(pool.Id, merged.AuxPoolId, StringComparison.OrdinalIgnoreCase));
        if(auxiliary == null)
            throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': Configured mergedMining.auxPoolId was not found; check the auxiliary pool id", parent.Id);
        if(string.Equals(auxiliary.Id, parent.Id, StringComparison.OrdinalIgnoreCase))
            throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': Parent and auxiliary pool ids must be different", parent.Id);
        if(!auxiliary.Enabled)
            throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': Auxiliary pool '{auxiliary.Id}' must be enabled", parent.Id);

        EnsurePaymentProcessing(auxiliary, "auxiliary");
        if(requireAssignedTemplates || parent.Template != null)
        {
            if(parent.Template is not BitcoinTemplate parentCoin ||
               !string.Equals(parentCoin.Symbol, "LTC", StringComparison.OrdinalIgnoreCase))
                throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': Merged mining currently requires Litecoin as the parent chain", parent.Id);
        }
        if(requireAssignedTemplates || auxiliary.Template != null)
        {
            if(auxiliary.Template is not BitcoinTemplate auxiliaryCoin)
                throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': Auxiliary pool '{auxiliary.Id}' must use the Bitcoin coin family", parent.Id);
            if(!string.Equals(auxiliaryCoin.Symbol, "DOGE", StringComparison.OrdinalIgnoreCase))
                throw new TrustedPoolStartupException($"Merged-mining parent pool '{parent.Id}': Auxiliary pool '{auxiliary.Id}' must use Dogecoin as the auxiliary chain", parent.Id);
        }

        if(string.IsNullOrWhiteSpace(auxiliary.Address))
            throw new PoolStartupException($"Merged-mining parent pool '{parent.Id}': Auxiliary pool '{auxiliary.Id}' requires a pool wallet address", parent.Id);
        if(auxiliary.Daemons == null || auxiliary.Daemons.Length == 0)
            throw new PoolStartupException($"Merged-mining parent pool '{parent.Id}': Auxiliary pool '{auxiliary.Id}' requires a daemon endpoint", parent.Id);
        if(!MergedMiningPasswordParser.IsValidAddressParameter(merged.AddressParameter))
            throw new PoolStartupException($"Merged-mining parent pool '{parent.Id}': mergedMining.addressParameter must not be 'd' or contain ';' or '='", parent.Id);
        if(auxiliary.PaymentProcessing.PayoutScheme != PayoutScheme.SOLO && !merged.RequireAuxAddress)
            throw new PoolStartupException($"Merged-mining parent pool '{parent.Id}': For pooled auxiliary accounting, mergedMining.requireAuxAddress must be true so every accepted proof has a payout beneficiary", parent.Id);

        return auxiliary;
    }

    private static void EnsurePaymentProcessing(PoolConfig pool, string role)
    {
        if(pool.PaymentProcessing?.Enabled != true)
            throw new PoolStartupException(
                $"Merged-mining {role} pool '{pool.Id}' requires enabled payment processing for accounting; to pause payouts, keep both pool switches enabled and set cluster paymentProcessing.paused=true",
                pool.Id);
        if(pool.PaymentProcessing.PayoutScheme is not (PayoutScheme.SOLO or
               PayoutScheme.PPS or PayoutScheme.PROP or PayoutScheme.PPLNS))
            throw new PoolStartupException(
                $"Merged-mining {role} pool '{pool.Id}' must use SOLO, PPS, PROP or PPLNS payment processing", pool.Id);
    }
}
