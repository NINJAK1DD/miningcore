using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Time;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningBitcoinJobManager : BitcoinJobManager
{
    public MergedMiningBitcoinJobManager(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private MergedMiningConfig mergedMiningConfig;
    private PoolConfig auxiliaryPoolConfig;
    private BitcoinTemplate parentCoin;
    private BitcoinTemplate auxiliaryCoin;
    private RpcClient auxiliaryRpc;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        parentCoin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);

        mergedMiningConfig = pc.Extra.SafeExtensionDataAs<MergedMiningPoolConfigExtra>()?.MergedMining;
        if(mergedMiningConfig?.Enabled != true)
            return;

        auxiliaryPoolConfig = cc.Pools.FirstOrDefault(x =>
            string.Equals(x.Id, mergedMiningConfig.AuxPoolId, StringComparison.OrdinalIgnoreCase));

        if(auxiliaryPoolConfig?.Template is BitcoinTemplate template)
            auxiliaryCoin = template;

        var serializerSettings = ctx.Resolve<JsonSerializerSettings>();
        if(auxiliaryPoolConfig?.Daemons?.Length > 0)
            auxiliaryRpc = new RpcClient(auxiliaryPoolConfig.Daemons.First(), serializerSettings,
                messageBus, auxiliaryPoolConfig.Id);
    }
}
