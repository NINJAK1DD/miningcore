using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class MergedMiningPayoutRegtestTests
{
    [MergedMiningDaemonIntegrationFact]
    public async Task PinnedDaemons_WalletMaturityCannotBypassCustodialThreshold()
    {
        ModuleInitializer.Initialize();
        foreach(var chain in new[] { RegtestChain.Litecoin, RegtestChain.Dogecoin })
        {
            await using var node = await RegtestNode.StartAsync(chain);
            var litecoin = chain == RegtestChain.Litecoin;
            var coin = litecoin ? "litecoin" : "dogecoin";
            var threshold = litecoin ? 102 : 251;
            var maturity = litecoin ? 101 : 61;
            var address = (await node.RpcAsync("getnewaddress", true)).Value<string>();
            Task<JToken> Mine(int count) => litecoin
                ? node.RpcAsync("generatetoaddress", false, count, address)
                : node.RpcAsync("generate", true, count);
            var hash = (await Mine(1)).First.Value<string>();
            var rawBlock = await node.RpcAsync("getblock", false, hash);
            var txid = rawBlock["tx"].First.Value<string>();
            var rawTx = await node.RpcAsync("getrawtransaction", false, txid, true);
            var reward = rawTx["vout"].Sum(output => output["value"].Value<decimal>());
            var config = new PoolConfig
            {
                Id = coin, Template = ModuleInitializer.CoinTemplates[coin],
                Daemons = new[] { node.WalletEndpoint },
                PaymentProcessing = new PoolPaymentProcessingConfig(),
                Extra = new Dictionary<string, object> { ["minimumConfirmations"] = threshold },
            };
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(config);
            var context = ModuleInitializer.Container;
            var handler = new BitcoinPayoutHandler(context, Substitute.For<IConnectionFactory>(),
                context.Resolve<IMapper>(), Substitute.For<IShareRepository>(),
                Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
                Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(),
                Substitute.For<IMessageBus>(), new ActiveBlockGracePeriodTracker());
            await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
            Block Candidate() => new()
            {
                PoolId = coin, Type = litecoin ? "merged-parent" : "auxpow", Hash = hash,
                BlockHeight = rawBlock["height"].Value<ulong>(), TransactionConfirmationData = txid,
                Status = BlockStatus.Pending, Created = DateTime.UtcNow.AddHours(-1),
            };
            var block = Candidate();
            await Mine(maturity - 1);
            var confirmations = maturity;
            foreach(var target in new[] { maturity, threshold - 1, threshold, threshold + 1 }.Distinct())
            {
                if(target > confirmations) await Mine(target - confirmations);
                confirmations = target;
                var wallet = await node.RpcAsync("gettransaction", true, txid);
                Assert.Equal("generate", wallet["details"][0]["category"].Value<string>());
                Assert.Equal(target, wallet["confirmations"].Value<int>());
                await handler.ClassifyBlocksAsync(pool, new[] { block }, CancellationToken.None);
                Assert.Equal(target >= threshold ? BlockStatus.Confirmed : BlockStatus.Pending, block.Status);
                Assert.Equal(target >= threshold, block.NotifyBlockUnlockedOnUpdate);
                Assert.Equal(reward, block.Reward);
                Assert.Equal(txid, block.TransactionConfirmationData);
                if(target < threshold) Assert.InRange(block.ConfirmationProgress, 0, 0.999d);
            }
            await node.RpcAsync("invalidateblock", false, hash);
            block = Candidate();
            await handler.ClassifyBlocksAsync(pool, new[] { block }, CancellationToken.None);
            Assert.Equal(BlockStatus.Orphaned, block.Status);
            await node.RpcAsync("reconsiderblock", false, hash);
            block = Candidate();
            await handler.ClassifyBlocksAsync(pool, new[] { block }, CancellationToken.None);
            Assert.Equal(BlockStatus.Confirmed, block.Status);
        }
    }
}
