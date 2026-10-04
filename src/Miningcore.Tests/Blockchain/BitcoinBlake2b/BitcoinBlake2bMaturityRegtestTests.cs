using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.JsonRpc;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using RewardBlock = Miningcore.Persistence.Model.Block;
using BlockStatus = Miningcore.Persistence.Model.BlockStatus;
using Node = Miningcore.Tests.Blockchain.Bitcoin.BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class BitcoinBlake2bMaturityRegtestTests : TestBase
{
    private static BitcoinBlake2bTemplate Coin(int start, int enforce, int release)
    {
        var coin = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "coins.json")))["bitcoin-blake2b"].ToObject<BitcoinBlake2bTemplate>();
        var network = coin.Networks["regtest"];
        network.Blake2bMaturityStart = start;
        network.Blake2bMaturityEnforce = enforce;
        network.Blake2bMaturityRelease = release;
        return coin;
    }

    private sealed class ReconciliationHandler : BitcoinBlake2bPayoutHandler
    {
        internal Func<Task> AfterWalletLookup;
        internal bool ContractUnavailable;
        internal bool ContractChanged;
        internal ReconciliationHandler(IComponentContext context, IPaymentRepository payments) : base(context,
            Substitute.For<IConnectionFactory>(), AutoMapperFactory.CreateMapper(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            payments, new StandardClock(), Substitute.For<IMessageBus>(), new ActiveBlockGracePeriodTracker(), new BitcoinBlake2bPayoutContractTracker()) { }

        protected override async Task<RpcResponse<JToken>[]> GetTransactionsAsync(RewardBlock[] blocks, CancellationToken ct)
        {
            var results = await base.GetTransactionsAsync(blocks, ct);
            var callback = AfterWalletLookup;
            AfterWalletLookup = null;
            if(callback != null) await callback();
            return results;
        }

        protected override async Task<RpcResponse<JObject>> ReadBlake2bPayoutContractAsync(string method, CancellationToken ct)
        {
            if(ContractUnavailable) return new(null, new JsonRpcError(-500, "Unavailable", null));
            var response = await base.ReadBlake2bPayoutContractAsync(method, ct);
            if(ContractChanged && method == "getdeploymentinfo")
                response.Response["deployments"]["long_coinbase_maturity"]["expiry_time"] = 1819756800;
            return response;
        }
    }

    private async Task<(ReconciliationHandler Handler, IMiningPool Pool, IPaymentRepository Payments)> Handler(Node node, BitcoinBlake2bTemplate coin)
    {
        var config = new PoolConfig
        {
            Id = "blake2b-maturity", Coin = "bitcoin-blake2b", Template = coin,
            Address = await node.GetNewAddressAsync(), Daemons = new[] { node.WalletEndpoint },
            Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 1 },
            PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = PayoutScheme.SOLO },
        };
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        var payments = Substitute.For<IPaymentRepository>();
        var handler = new ReconciliationHandler(container, payments);
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
        return (handler, pool, payments);
    }

    private static async Task<RewardBlock> Reward(Node node, int height)
    {
        var hash = (await node.RootRpcAsync("getblockhash", height)).Value<string>();
        var block = await node.RootRpcAsync("getblock", hash);
        return new RewardBlock
        {
            PoolId = "blake2b-maturity", BlockHeight = (ulong) height, Hash = hash,
            TransactionConfirmationData = block["tx"][0].Value<string>(), Status = BlockStatus.Pending,
            Created = DateTime.UtcNow,
        };
    }

    [BitcoinBlake2bIntegrationFact]
    public async Task WalletMaturity_ReleaseRestartReorgAndReconsiderKeepPendingRewardsTruthful()
    {
        await using var node = await Node.StartAsync(true,
            Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
            new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest", "-testcoinbasematuritylong=30:134:140" }, 130);
        var coin = Coin(30, 134, 140);
        var fixture = await Handler(node, coin);
        var unsupported = new RewardBlock { SettlementMode = BitcoinDirectCoinbaseSettlement.Mode };
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { unsupported }, CancellationToken.None);
        Assert.Equal(BlockStatus.Quarantined, unsupported.Status);
        var older = await Reward(node, 29);
        var covered = await Reward(node, 30);
        async Task Classify() => await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { older, covered }, CancellationToken.None);
        await Classify();
        Assert.Equal(BlockStatus.Pending, older.Status); // all-coinbase wallet policy, even before coverage
        Assert.Equal(BlockStatus.Pending, covered.Status);
        Assert.Equal(102.0 / 111, older.ConfirmationProgress, 12);
        Assert.Equal(101.0 / 111, covered.ConfirmationProgress, 12);
        Assert.Equal(50m, older.Reward);
        Assert.Equal(50m, covered.Reward);
        Assert.Equal(0m, (await node.WalletRpcAsync("gettransaction", covered.TransactionConfirmationData))["amount"].Value<decimal>());
        // Ordinary transactions are not subject to the coinbase lock.
        var destination = await node.GetNewAddressAsync();
        var parent = (await node.WalletRpcAsync("sendtoaddress", destination, 1m)).Value<string>();
        var decoded = await node.RootRpcAsync("getrawtransaction", parent, true);
        var parentOutput = ((JArray) decoded["vout"]).Single(x => x["scriptPubKey"]["address"]?.Value<string>() == destination);
        var child = await node.RootRpcAsync("createrawtransaction", new[] { new { txid = parent, vout = parentOutput["n"].Value<int>() } },
            new Dictionary<string, decimal> { [await node.GetNewAddressAsync()] = 0.999m });
        var signed = await node.WalletRpcAsync("signrawtransactionwithwallet", child.Value<string>());
        Assert.True(signed["complete"].Value<bool>());
        await node.RootRpcAsync("sendrawtransaction", signed["hex"].Value<string>());
        await node.GenerateAsync(3, CancellationToken.None); // tip133, next134 enforces
        var enforcing = (JObject) await node.RootRpcAsync("getdeploymentinfo");
        Assert.True(enforcing["deployments"]["long_coinbase_maturity"]["active"].Value<bool>());
        BitcoinBlake2bMaturity.ForNetwork(coin, "regtest", "test").ValidateDeployment(enforcing, "test");
        var enforcingRules = (await node.RootRpcAsync("getblocktemplate", new { rules = new[] { "segwit", "blake2b" } }))["rules"].ToObject<string[]>();
        Assert.Contains("long_coinbase_maturity", enforcingRules);
        await node.GenerateAsync(6, CancellationToken.None); // tip139, next140 is inactive
        var deployment = (JObject) await node.RootRpcAsync("getdeploymentinfo");
        Assert.False(deployment["deployments"]["long_coinbase_maturity"]["active"].Value<bool>());
        var rules = (await node.RootRpcAsync("getblocktemplate", new { rules = new[] { "segwit", "blake2b" } }))["rules"].ToObject<string[]>();
        Assert.DoesNotContain("long_coinbase_maturity", rules);
        await Classify();
        Assert.Equal(BlockStatus.Confirmed, older.Status); //111 confirmations
        Assert.Equal(BlockStatus.Pending, covered.Status); //110 confirmations; inactivity cannot unlock
        Assert.Equal(110.0 / 111, covered.ConfirmationProgress, 12);
        // Reorg between the wallet batch and active-chain lookup: the old
        // generate response must not unlock using a stale confirmation count.
        var tip = (await node.RootRpcAsync("getbestblockhash")).Value<string>();
        fixture.Handler.AfterWalletLookup = async () => await node.RootRpcAsync("invalidateblock", tip);
        await Classify();
        Assert.Equal(BlockStatus.Pending, older.Status);
        Assert.Equal(110.0 / 111, older.ConfirmationProgress, 12);
        await node.RootRpcAsync("reconsiderblock", tip);
        await Classify();
        Assert.Equal(BlockStatus.Confirmed, older.Status);
        fixture.Handler.ContractUnavailable = true;
        await Assert.ThrowsAsync<InvalidOperationException>(Classify);
        Assert.Equal(BlockStatus.Pending, covered.Status);
        fixture.Handler.ContractUnavailable = false;
        fixture.Handler.ContractChanged = true;
        await Assert.ThrowsAsync<InvalidOperationException>(Classify);
        Assert.Equal(BlockStatus.Pending, covered.Status);
        fixture.Handler.ContractChanged = false;
        fixture = await Handler(node, coin); // new handler after restart/upgrade reconciliation
        await Classify();
        Assert.Equal(BlockStatus.Pending, covered.Status);
        await node.GenerateAsync(1, CancellationToken.None);
        await Classify();
        Assert.Equal(BlockStatus.Confirmed, covered.Status);
        Assert.Equal(1, covered.ConfirmationProgress);
        // A pending record whose coinbase leaves the active chain is orphaned;
        // the share/PPS ledger is not part of reward classification.
        covered.Status = BlockStatus.Pending;
        await node.RootRpcAsync("invalidateblock", covered.Hash);
        await Classify();
        Assert.Equal(BlockStatus.Orphaned, covered.Status);
        await node.RootRpcAsync("reconsiderblock", covered.Hash);
        covered.Status = BlockStatus.Pending;
        fixture = await Handler(node, coin);
        await Classify();
        Assert.Equal(BlockStatus.Confirmed, covered.Status);
        Assert.Empty(fixture.Payments.ReceivedCalls()); // classification cannot submit/persist payments
    }

    [BitcoinBlake2bIntegrationFact]
    public async Task PrunedCoinbase_UsesHeadersToMatureAndReconcileWithoutBlockBodies()
    {
        await using var node = await Node.StartAsync(true,
            Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
            new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest",
                "-testcoinbasematuritylong=30:1034:1040", "-prune=1", "-fastprune=1",
                "-uacomment=Miningcore review test", "-uaappend=MiningcoreTest:1" }, 0);
        async Task Generate(int count)
        {
            // Keep each RPC below the fixture's bounded HTTP timeout on Windows.
            while(count > 0)
            {
                var batch = Math.Min(count, 50);
                await node.GenerateAsync(batch, CancellationToken.None);
                count -= batch;
            }
        }
        await Generate(500);
        var reward = await Reward(node, 30);
        var fixture = await Handler(node, Coin(30, 1034, 1040));
        await node.RootRpcAsync("pruneblockchain", 210);
        var pruned = await node.TryWalletRpcAsync("getblock", reward.Hash);
        Assert.NotNull(pruned.Error);
        Assert.Equal(-1, pruned.Error["code"].Value<int>());
        Assert.Contains("pruned", pruned.Error["message"].Value<string>(), StringComparison.OrdinalIgnoreCase);
        var header = await node.RootRpcAsync("getblockheader", reward.Hash);
        Assert.Equal(30, header["height"].Value<int>());
        Assert.Equal(471, header["confirmations"].Value<int>());
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, reward.Status);
        Assert.Equal(50m, reward.Reward);
        Assert.Equal(471.0 / 1011, reward.ConfirmationProgress, 12);
        await Generate(540);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, reward.Status);
        Assert.Equal(50m, reward.Reward);
        Assert.NotNull((await node.TryWalletRpcAsync("getblock", reward.Hash)).Error);
        var tip = (await node.RootRpcAsync("getbestblockhash")).Value<string>();
        await node.RootRpcAsync("invalidateblock", tip);
        fixture = await Handler(node, Coin(30, 1034, 1040));
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, reward.Status);
        Assert.Equal(1010.0 / 1011, reward.ConfirmationProgress, 12);
        await node.RootRpcAsync("reconsiderblock", tip);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, reward.Status);
        Assert.Empty(fixture.Payments.ReceivedCalls());
    }

    [BitcoinBlake2bIntegrationFact]
    public async Task ExplicitRegtestSchedule_AllowsEnforcementBeforeCoveredCoinbaseStart()
    {
        await using var node = await Node.StartAsync(true,
            Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
            new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest",
                "-testcoinbasematuritylong=30:2:140" }, 31);
        var coin = Coin(30, 2, 140);
        var schedule = BitcoinBlake2bMaturity.ForNetwork(coin, "regtest", "test");
        schedule.ValidateDeployment((JObject) await node.RootRpcAsync("getdeploymentinfo"), "test");
        var fixture = await Handler(node, coin);
        var reward = await Reward(node, 29);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, reward.Status);
        Assert.Equal(50m, reward.Reward);
        Assert.Equal(3.0 / 111, reward.ConfirmationProgress, 12);
    }

    [BitcoinBlake2bIntegrationFact]
    public async Task WalletRejectsImmatureFunds_LeavesPaymentsUnbooked()
    {
        await using var node = await Node.StartAsync(true,
            Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
            new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest", "-testcoinbasematuritylong=2:201:202" }, 110);
        var fixture = await Handler(node, Coin(2, 201, 202));
        var reward = await Reward(node, 1);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, reward.Status);
        Assert.Equal(110.0 / 201, reward.ConfirmationProgress, 12);
        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Miningcore.Persistence.Model.Balance { PoolId = fixture.Pool.Config.Id,
                Address = await node.GetNewAddressAsync(), Amount = 1 },
        }, CancellationToken.None);
        Assert.Empty(fixture.Payments.ReceivedCalls());
        Assert.Empty((await node.RootRpcAsync("getrawmempool")).ToObject<string[]>());
        Assert.Equal(BlockStatus.Pending, reward.Status);
    }
}
