using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NBitcoin;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinDirectSoloRegtestTests : TestBase
{
    [BitcoinCoreIntegrationFact]
    public async Task DirectJob_SubmitsAndConfirmsWithoutDestinationWalletOwnership()
    {
        await using var node = await BitcoinPayoutHandlerRegtestTests
            .BitcoinCoreRegtestNode.StartAsync(walletBroadcast: true);
        var rawTemplate = Assert.IsType<JObject>(await node.RootRpcAsync(
            "getblocktemplate", new JObject
            {
                ["rules"] = new JArray("segwit"),
            }));
        var blockTemplate = rawTemplate.ToObject<BlockTemplate>();
        var coin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin"]);
        var miner = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var fee = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var recipients = BitcoinDirectCoinbase.ValidateRecipients(new[]
        {
            new RewardRecipient
            {
                Address = fee.ToString(),
                Percentage = 2m,
            },
        }, value => BitcoinAddress.Create(value, Network.RegTest));
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var poolConfig = new PoolConfig
        {
            Id = $"bitcoin-direct-regtest-{Guid.NewGuid():N}",
            Coin = "bitcoin",
            Template = coin,
            Daemons = new[] { node.WalletEndpoint },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            RewardRecipients = new[]
            {
                new RewardRecipient
                {
                    Address = fee.ToString(),
                    Percentage = 2m,
                },
            },
        };
        var job = new BitcoinJob();
        job.InitDirect(blockTemplate, "direct-regtest", poolConfig,
            new BitcoinPoolConfigExtra { SoloCoinbasePayout = true },
            new ClusterConfig(), clock, miner, Network.RegTest, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue,
            new BitcoinDirectCoinbaseTemplate
            {
                MinerAddress = miner.ToString(),
                MinerDestination = miner,
                MinerScriptPubKey = miner.ScriptPubKey.ToHex(),
                Recipients = recipients,
            });
        var worker = CreateWorker(clock, miner.ToString());

        (Miningcore.Blockchain.Share Share, string BlockHex) candidate = default;
        for(uint nonce = 0; nonce < 1_000_000; nonce++)
        {
            candidate = job.ProcessShare(worker, "00000000000000",
                blockTemplate.CurTime.ToStringHex8(), nonce.ToStringHex8());
            if(candidate.Share.IsBlockCandidate)
                break;
        }

        Assert.True(candidate.Share?.IsBlockCandidate == true,
            "Miningcore did not produce a regtest block candidate");
        Assert.False(string.IsNullOrEmpty(candidate.BlockHex));
        Assert.Equal(BitcoinDirectCoinbaseSettlement.Mode,
            candidate.Share.SettlementMode);

        var submitResult = await node.RootRpcAsync("submitblock",
            candidate.BlockHex);
        Assert.Equal(JTokenType.Null, submitResult.Type);

        var accepted = Assert.IsType<JObject>(await node.RootRpcAsync(
            "getblock", candidate.Share.BlockHash, 2));
        var coinbase = Assert.IsType<JObject>(
            Assert.IsType<JArray>(accepted["tx"])[0]);
        Assert.Equal(coinbase.Value<string>("txid"),
            candidate.Share.TransactionConfirmationData);
        var outputs = Assert.IsType<JArray>(coinbase["vout"]);
        var minerAmount = outputs.Where(x => string.Equals(
                x["scriptPubKey"]?["hex"]?.Value<string>(),
                miner.ScriptPubKey.ToHex(), StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Value<decimal>("value"));
        var feeAmount = outputs.Where(x => string.Equals(
                x["scriptPubKey"]?["hex"]?.Value<string>(),
                fee.ScriptPubKey.ToHex(), StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Value<decimal>("value"));

        Assert.Equal(49m, minerAmount);
        Assert.Equal(1m, feeAmount);
        Assert.Contains(outputs, x => x["scriptPubKey"]?["hex"]?
            .Value<string>()?.StartsWith("6a24aa21a9ed",
                StringComparison.OrdinalIgnoreCase) == true);

        var block = new Miningcore.Persistence.Model.Block
        {
            PoolId = poolConfig.Id,
            BlockHeight = blockTemplate.Height,
            Hash = candidate.Share.BlockHash,
            TransactionConfirmationData = coinbase.Value<string>("txid"),
            Status = BlockStatus.Pending,
            Type = BitcoinDirectCoinbaseSettlement.BlockType,
            SettlementMode = candidate.Share.SettlementMode,
            GrossRewardSatoshis = candidate.Share.GrossRewardSatoshis,
            DirectMinerRewardSatoshis =
                candidate.Share.DirectMinerRewardSatoshis,
            DirectMinerScriptPubKey =
                candidate.Share.DirectMinerScriptPubKey,
            DirectRecipientOutputs =
                candidate.Share.DirectRecipientOutputs,
            DirectSubmissionState =
                BitcoinDirectSubmission.ObservedActive,
            DirectSubmissionBlock = candidate.BlockHex,
            DirectSubmissionAttempts = 1,
            DirectSubmissionDefinitiveMisses = 0,
            DirectSubmissionLastAttempt = DateTime.UtcNow,
            Created = DateTime.UtcNow,
        };
        var miningPool = Substitute.For<Miningcore.Mining.IMiningPool>();
        miningPool.Config.Returns(poolConfig);
        var handler = await CreateHandlerAsync(poolConfig, clock);

        var pending = Assert.Single(await handler.ClassifyBlocksAsync(
            miningPool, new[] { block }, CancellationToken.None));
        Assert.Equal(BlockStatus.Pending, pending.Status);
        Assert.True(pending.ConfirmationProgress > 0);

        await node.GenerateAsync(101, CancellationToken.None);
        handler = await CreateHandlerAsync(poolConfig, clock);

        var classified = Assert.Single(await handler.ClassifyBlocksAsync(
            miningPool, new[] { block }, CancellationToken.None));

        Assert.Equal(BlockStatus.Confirmed, classified.Status);
        Assert.Equal(50m, classified.Reward);
        Assert.Equal(1d, classified.ConfirmationProgress);

        await node.RootRpcAsync("invalidateblock",
            candidate.Share.BlockHash);
        handler = await CreateHandlerAsync(poolConfig, clock);
        var orphaned = Assert.Single(await handler.ClassifyBlocksAsync(
            miningPool, new[] { block }, CancellationToken.None));
        Assert.Equal(BlockStatus.Orphaned, orphaned.Status);
        Assert.Equal(0, orphaned.Reward);

        await node.RootRpcAsync("reconsiderblock",
            candidate.Share.BlockHash);
        handler = await CreateHandlerAsync(poolConfig, clock);
        var reactivated = Assert.Single(await handler.ClassifyBlocksAsync(
            miningPool, new[] { block }, CancellationToken.None));
        Assert.Equal(BlockStatus.Confirmed, reactivated.Status);
        Assert.Equal(50m, reactivated.Reward);
        Assert.Equal(1d, reactivated.ConfirmationProgress);
    }

    private async Task<BitcoinPayoutHandler> CreateHandlerAsync(
        PoolConfig poolConfig, IMasterClock clock)
    {
        var handler = new BitcoinPayoutHandler(container,
            Substitute.For<IConnectionFactory>(),
            container.Resolve<AutoMapper.IMapper>(),
            Substitute.For<IShareRepository>(),
            Substitute.For<Miningcore.Persistence.Repositories.IBlockRepository>(),
            Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), clock,
            Substitute.For<IMessageBus>(),
            new ActiveBlockGracePeriodTracker());
        await handler.ConfigureAsync(new ClusterConfig(), poolConfig,
            CancellationToken.None);
        return handler;
    }

    private Miningcore.Stratum.StratumConnection CreateWorker(IMasterClock clock,
        string miner)
    {
        var worker = new Miningcore.Stratum.StratumConnection(
            new NLog.NullLogger(NLog.LogManager.LogFactory),
            container.Resolve<Microsoft.IO.RecyclableMemoryStreamManager>(),
            clock, "direct-regtest", false);
        worker.SetContext(new BitcoinWorkerContext
        {
            Miner = miner,
            ExtraNonce1 = "00000001",
            Difficulty = 0.000000000001d,
        });
        return worker;
    }
}
