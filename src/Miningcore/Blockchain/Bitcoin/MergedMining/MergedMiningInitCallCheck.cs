using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

public class MergedMiningBitcoinJobManagerInitCallCheck : BitcoinJobManager
{
    public MergedMiningBitcoinJobManagerInitCallCheck(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private BitcoinTemplate parentCoin;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        parentCoin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);
    }

    private MergedMiningBitcoinJob CreateMergedJob(BlockTemplate parent, AuxBlockTemplate auxiliary)
    {
        var job = new MergedMiningBitcoinJob();
        job.InitMerged(parent, auxiliary, NextJobId(), poolConfig, extraPoolConfig,
            clusterConfig, clock, poolAddressDestination, network, isPoS, ShareMultiplier,
            parentCoin.CoinbaseHasherValue, parentCoin.HeaderHasherValue,
            !isPoS ? parentCoin.BlockHasherValue : parentCoin.PoSBlockHasherValue ?? parentCoin.BlockHasherValue);
        return job;
    }
}
