using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
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

    public override async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await base.RunAsync(ct);
        }
        finally
        {
            // StratumConnection stops awaiting request processing when a socket or host task
            // ends. Validated candidates are manager-owned, so keep the pool alive until their
            // daemon submission and durable database/recovery-journal paths have completed.
            if(manager is MergedMiningBitcoinJobManager mergedManager)
                await mergedManager.DrainCandidateOperationsAsync();
        }
    }

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        mergedMiningConfig = MergedMiningConfigLoader.GetNormalizedConfig(pc);
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
        {
            PublishAttributionRejection(MergedMiningAttributionRejection.Missing);
            return false;
        }

        if(!string.IsNullOrWhiteSpace(auxiliaryAddress))
        {
            var validation = await mergedManager.ValidateAuxiliaryAddressAsync(auxiliaryAddress, ct);

            if(validation == AuxiliaryAddressValidation.Invalid)
            {
                PublishAttributionRejection(MergedMiningAttributionRejection.Invalid);
                return false;
            }

            if(validation == AuxiliaryAddressValidation.Unavailable)
            {
                PublishAttributionRejection(
                    MergedMiningAttributionRejection.ValidationUnavailable);
                throw new StratumException(StratumError.Other,
                    "auxiliary payout-address validation is temporarily unavailable");
            }
        }

        if(context is not MergedMiningBitcoinWorkerContext mergedContext)
            throw new InvalidOperationException("Merged-mining worker context is not configured");

        mergedContext.AuxiliaryMiner = auxiliaryAddress;
        return true;
    }

    private void PublishAttributionRejection(
        MergedMiningAttributionRejection reason) =>
        messageBus.SendMessage(new MergedMiningAttributionRejectedTelemetryEvent(
            poolConfig.Id, mergedMiningConfig.AuxPoolId, reason));
}
