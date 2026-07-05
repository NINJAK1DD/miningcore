using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

[CoinFamily(CoinFamily.Bitcoin)]
public class MergedMiningBitcoinPool : BitcoinPool
{
    public MergedMiningBitcoinPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    private MergedMiningConfig mergedMiningConfig;

    private bool MergedMiningEnabled => mergedMiningConfig?.Enabled == true;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        mergedMiningConfig = pc.Extra.SafeExtensionDataAs<MergedMiningPoolConfigExtra>()?.MergedMining;
        base.Configure(pc, cc);
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new MergedMiningBitcoinWorkerContext();
    }

    protected override async Task<bool> ValidateWorkerAsync(BitcoinWorkerContext context,
        string minerName, string password, CancellationToken ct)
    {
        if(!MergedMiningEnabled)
            return await base.ValidateWorkerAsync(context, minerName, password, ct);

        if(manager is not MergedMiningBitcoinJobManager mergedManager)
            throw new InvalidOperationException("Merged-mining job manager is not configured");

        // Reject an invalid parent payout address before making an auxiliary RPC call.
        if(!await mergedManager.ValidateAddressAsync(minerName, ct))
            return false;

        var auxiliaryAddress = MergedMiningPasswordParser.GetValue(password,
            mergedMiningConfig.AddressParameter);

        if(string.IsNullOrWhiteSpace(auxiliaryAddress) && mergedMiningConfig.RequireAuxAddress)
            throw new StratumException(StratumError.UnauthorizedWorker,
                $"missing {mergedMiningConfig.AddressParameter} auxiliary address in password");

        if(!string.IsNullOrWhiteSpace(auxiliaryAddress))
        {
            if(!await mergedManager.ValidateAuxiliaryAddressAsync(auxiliaryAddress, ct))
                throw new StratumException(StratumError.UnauthorizedWorker,
                    "invalid auxiliary payout address");
        }

        if(context is not MergedMiningBitcoinWorkerContext mergedContext)
            throw new InvalidOperationException("Merged-mining worker context is not configured");

        mergedContext.AuxiliaryMiner = auxiliaryAddress;
        return true;
    }
}
