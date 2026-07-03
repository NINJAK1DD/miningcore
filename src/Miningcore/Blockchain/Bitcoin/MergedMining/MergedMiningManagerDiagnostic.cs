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
    private BitcoinTemplate parentCoin;
    private BitcoinTemplate auxiliaryCoin;
    private RpcClient auxiliaryRpc;

    private bool MergedMiningEnabled => mergedMiningConfig?.Enabled == true;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        parentCoin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);

        mergedMiningConfig = pc.Extra.SafeExtensionDataAs<MergedMiningPoolConfigExtra>()?.MergedMining;
        if(!MergedMiningEnabled)
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

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct,
        bool forceUpdate, string via = null, string json = null)
    {
        if(!MergedMiningEnabled)
            return await base.UpdateJob(ct, forceUpdate, via, json);

        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

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

            var parentIsNew = previousJob == null ||
                previousJob.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                blockTemplate.Height > previousJob.BlockTemplate?.Height;

            var auxiliaryIsNew = previousJob == null ||
                !string.Equals(previousJob.AuxiliaryBlockTemplate?.Hash, auxiliaryTemplate.Hash,
                    StringComparison.OrdinalIgnoreCase);

            if(parentIsNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(auxiliaryIsNew)
                messageBus.NotifyChainHeight(auxiliaryPoolConfig.Id, auxiliaryTemplate.Height, auxiliaryPoolConfig.Template);

            if(parentIsNew || auxiliaryIsNew || forceUpdate)
            {
                var job = new MergedMiningBitcoinJob();
                job.InitMerged(blockTemplate, auxiliaryTemplate, NextJobId(), poolConfig,
                    extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, parentCoin.CoinbaseHasherValue, parentCoin.HeaderHasherValue,
                    !isPoS ? parentCoin.BlockHasherValue : parentCoin.PoSBlockHasherValue ?? parentCoin.BlockHasherValue);

                if(parentIsNew)
                {
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                currentJob = job;
            }

            return (parentIsNew || auxiliaryIsNew, forceUpdate);
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    public async Task<bool> ValidateAuxiliaryAddressAsync(string address, CancellationToken ct)
    {
        if(!MergedMiningEnabled)
            return true;

        if(string.IsNullOrWhiteSpace(address))
            return !mergedMiningConfig.RequireAuxAddress;

        var result = await auxiliaryRpc.ExecuteAsync<ValidateAddressResponse>(logger,
            BitcoinCommands.ValidateAddress, ct, new[] { address });

        return result.Error == null && result.Response is { IsValid: true };
    }
}
