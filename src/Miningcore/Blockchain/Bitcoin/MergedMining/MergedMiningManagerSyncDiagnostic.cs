using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
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

    private const string CreateAuxBlock = "createauxblock";
    private MergedMiningConfig mergedMiningConfig;
    private PoolConfig auxiliaryPoolConfig;
    private RpcClient auxiliaryRpc;

    private bool MergedMiningEnabled => mergedMiningConfig?.Enabled == true;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        base.Configure(pc, cc);

        mergedMiningConfig = pc.Extra.SafeExtensionDataAs<MergedMiningPoolConfigExtra>()?.MergedMining;
        if(!MergedMiningEnabled)
            return;

        auxiliaryPoolConfig = cc.Pools.FirstOrDefault(x =>
            string.Equals(x.Id, mergedMiningConfig.AuxPoolId, StringComparison.OrdinalIgnoreCase));

        var serializerSettings = ctx.Resolve<JsonSerializerSettings>();
        if(auxiliaryPoolConfig?.Daemons?.Length > 0)
            auxiliaryRpc = new RpcClient(auxiliaryPoolConfig.Daemons.First(), serializerSettings,
                messageBus, auxiliaryPoolConfig.Id);
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        await base.EnsureDaemonsSynchedAsync(ct);

        if(!MergedMiningEnabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        do
        {
            var response = await GetAuxBlockTemplateAsync(ct);
            if(response.Error == null && response.Response != null)
                return;
        } while(await timer.WaitForNextTickAsync(ct));
    }

    private Task<RpcResponse<AuxBlockTemplate>> GetAuxBlockTemplateAsync(CancellationToken ct)
    {
        return auxiliaryRpc.ExecuteAsync<AuxBlockTemplate>(logger, CreateAuxBlock, ct,
            new[] { auxiliaryPoolConfig.Address });
    }
}
