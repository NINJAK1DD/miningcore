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

    private Task<RpcResponse<AuxBlockTemplate>> GetAuxBlockTemplateAsync(CancellationToken ct)
    {
        return auxiliaryRpc.ExecuteAsync<AuxBlockTemplate>(logger, CreateAuxBlock, ct,
            new[] { auxiliaryPoolConfig.Address });
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct,
        bool forceUpdate, string via = null, string json = null)
    {
        if(!MergedMiningEnabled)
            return await base.UpdateJob(ct, forceUpdate, via, json);

        var parentResponse = string.IsNullOrEmpty(json)
            ? await GetBlockTemplateAsync(ct)
            : GetBlockTemplateFromJson(json);

        if(parentResponse.Error != null || parentResponse.Response == null)
            return (false, forceUpdate);

        var auxiliaryResponse = await GetAuxBlockTemplateAsync(ct);
        if(auxiliaryResponse.Error != null || auxiliaryResponse.Response == null)
            return (false, forceUpdate);

        var blockTemplate = parentResponse.Response;
        var auxiliaryTemplate = auxiliaryResponse.Response;
        var previousJob = currentJob as MergedMiningBitcoinJob;
        var previousHeight = previousJob?.BlockTemplate?.Height ?? 0;

        var parentIsNew = previousJob == null ||
            previousJob.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
            blockTemplate.Height > previousHeight;

        var auxiliaryIsNew = previousJob == null ||
            !string.Equals(previousJob.AuxiliaryBlockTemplate?.Hash, auxiliaryTemplate.Hash,
                StringComparison.OrdinalIgnoreCase);

        if(parentIsNew)
            messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

        if(auxiliaryIsNew)
            messageBus.NotifyChainHeight(auxiliaryPoolConfig.Id, auxiliaryTemplate.Height,
                auxiliaryPoolConfig.Template);

        return (parentIsNew || auxiliaryIsNew, forceUpdate);
    }
}
