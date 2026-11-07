using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class MergedMiningPayoutRegtestTests
{
    [NexaDaemonIntegrationFact]
    public async Task Nexa_RealCoinbaseWalletAndChainContractsEnforceMaturityAndPolicy()
    {
        ModuleInitializer.Initialize();
        await using var node = await RegtestNode.StartAsync(RegtestChain.Nexa);
        Assert.Contains("Nexa:2.2.0", (await node.RpcAsync("getnetworkinfo", false))["subversion"].Value<string>());
        var hash = (await node.RpcAsync("generate", true, 1)).First.Value<string>();
        var chain = await node.RpcAsync("getblock", false, hash);
        var txid = chain["txid"].First.Value<string>();
        Assert.NotEqual(txid, chain["txidem"].First.Value<string>());
        var raw = await node.RpcAsync("getrawtransaction", false, txid, true);
        var reward = raw["vout"].Sum(x => x["value"].Value<decimal>());
        var config = new PoolConfig
        {
            Id = "nexa-live", Template = ModuleInitializer.CoinTemplates["nexa"],
            Daemons = new[] { node.WalletEndpoint },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
            Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 102 },
        };
        var context = ModuleInitializer.Container;
        var handler = new BitcoinPayoutHandler(context, Substitute.For<IConnectionFactory>(),
            context.Resolve<IMapper>(), Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(), Substitute.For<IPaymentRepository>(),
            Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>(), new ActiveBlockGracePeriodTracker());
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
        Block Candidate() => new()
        {
            PoolId = config.Id, Hash = hash, BlockHeight = 1,
            TransactionConfirmationData = txid, Status = BlockStatus.Pending,
        };
        var block = Candidate();
        var captures = new JArray();
        foreach(var depth in new[] { 1, 100, 101, 102, 103 })
        {
            var height = (await node.RpcAsync("getblockcount", false)).Value<int>();
            if(depth > height) await node.RpcAsync("generate", true, depth - height);
            var wallet = await node.RpcAsync("gettransaction", true, txid);
            chain = await node.RpcAsync("getblock", false, hash);
            captures.Add(new JObject { ["depth"] = depth, ["wallet"] = wallet, ["block"] = chain });
            Assert.Equal(JTokenType.Boolean, wallet["generated"].Type);
            Assert.True(wallet["generated"].Value<bool>());
            Assert.Equal(JTokenType.Integer, wallet["blockindex"].Type);
            Assert.Equal(0, wallet["blockindex"].Value<int>());
            Assert.Equal(txid, wallet["txid"].Value<string>());
            Assert.Equal(hash, wallet["blockhash"].Value<string>());
            Assert.Equal(depth < 101 ? "immature" : "generate", wallet["details"][0]["category"].Value<string>());
            await handler.ClassifyBlocksAsync(pool, new[] { block }, CancellationToken.None);
            Assert.Equal(depth < 102 ? BlockStatus.Pending : BlockStatus.Confirmed, block.Status);
            Assert.Equal(depth >= 102, block.NotifyBlockUnlockedOnUpdate);
            if(depth >= 101) Assert.Equal(reward, block.Reward);
        }
        // A low operator override must still respect Nexa's actual wallet maturity.
        config.Extra["minimumConfirmations"] = 1;
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
        var secondHash = (await node.RpcAsync("generate", true, 1)).First.Value<string>();
        var second = await node.RpcAsync("getblock", false, secondHash);
        var immature = new Block { PoolId = config.Id, Hash = secondHash,
            BlockHeight = second["height"].Value<ulong>(), TransactionConfirmationData = second["txid"].First.Value<string>() };
        await handler.ClassifyBlocksAsync(pool, new[] { immature }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, immature.Status);
        Assert.False(immature.NotifyBlockUnlockedOnUpdate);
        await node.RpcAsync("invalidateblock", false, hash);
        block = Candidate();
        await handler.ClassifyBlocksAsync(pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        await node.RpcAsync("reconsiderblock", false, hash);
        block = Candidate();
        await handler.ClassifyBlocksAsync(pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
        var output = Environment.GetEnvironmentVariable("MININGCORE_TEST_NEXA_CAPTURE");
        if(!string.IsNullOrWhiteSpace(output))
            await File.WriteAllTextAsync(output, new JObject
            {
                ["daemon"] = "Nexa 2.2.0.0", ["network"] = "isolated regtest",
                ["archiveSha256"] = "83df20afc40f0e33c35b0212684f19bc699b655524c4d40fd03fb19d12b49de5",
                ["observations"] = captures,
            }.ToString(Formatting.Indented) + "\n");
    }
}
