using AutoMapper;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Api.Extensions;

public static class MiningPoolExtensions
{
    public static PoolInfo ToPoolInfo(this PoolConfig poolConfig, IMapper mapper, Persistence.Model.PoolStats stats, IMiningPool pool)
    {
        var poolInfo = mapper.Map<PoolInfo>(poolConfig);

        poolInfo.PoolStats = mapper.Map<PoolStats>(stats);
        poolInfo.NetworkStats = pool?.NetworkStats ?? mapper.Map<BlockchainStats>(stats);

        // pool wallet link
        var addressInfobaseUrl = poolConfig.Template.ExplorerAccountLink;
        if(!string.IsNullOrEmpty(addressInfobaseUrl))
            poolInfo.AddressInfoLink = string.Format(addressInfobaseUrl, poolInfo.Address);

        // pool fees
        poolInfo.PoolFeePercent = poolConfig.RewardRecipients != null ? (float) poolConfig.RewardRecipients.Sum(x => x.Percentage) : 0;

        // Build an explicit, family-aware public projection. Unknown runtime
        // fields and wallet credentials have no response DTO member and cannot
        // enter either pool endpoint by default.
        poolInfo.PaymentProcessing.Extra =
            PaymentProcessingExtraProjection.Create(
                poolConfig.Template.Family,
                poolConfig.PaymentProcessing.Extra);

        poolInfo.Ports = CreatePublicPoolEndpoints(poolConfig.Ports, mapper);
        return poolInfo;
    }

    private static Dictionary<int, ApiPoolEndpoint> CreatePublicPoolEndpoints(
        Dictionary<int, PoolEndpoint> ports, IMapper mapper)
    {
        if(ports == null)
            return null;

        // Deferred listener validation permits stale endpoint data on disabled
        // and relay-only pools. Enabled relay-only pools are still public API
        // resources, so omit unusable null entries instead of returning a 500.
        // Build a new dictionary of dedicated public DTOs. Their type boundary
        // prevents API serialization from exposing or aliasing runtime-only
        // listener configuration, even if PoolEndpoint gains new private members.
        return ports.Where(x => x.Value != null)
            .ToDictionary(x => x.Key,
                x => mapper.Map<ApiPoolEndpoint>(x.Value));
    }
}
