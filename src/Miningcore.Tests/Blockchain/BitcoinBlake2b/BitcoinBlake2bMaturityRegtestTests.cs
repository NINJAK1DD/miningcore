using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Persistence.Postgres.Repositories;
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
        await Assert.ThrowsAsync<BitcoinBlake2bPayoutAttestationException>(Classify);
        Assert.Equal(BlockStatus.Pending, covered.Status);
        fixture.Handler.ContractUnavailable = false;
        fixture.Handler.ContractChanged = true;
        await Assert.ThrowsAsync<BitcoinBlake2bPayoutAttestationException>(Classify);
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
        await node.RootRpcAsync("invalidateblock", covered.Hash);
        await Classify();
        Assert.Equal(BlockStatus.Orphaned, covered.Status);
        await node.RootRpcAsync("reconsiderblock", covered.Hash);
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

    [BitcoinBlake2bLedgerIntegrationFact]
    public async Task PersistedOrphan_ReactivationThroughPayoutManagerRestoresAllSchemesExactlyOnce()
    {
        foreach(var payoutScheme in new[] { PayoutScheme.SOLO, PayoutScheme.PROP, PayoutScheme.PPLNS, PayoutScheme.PPS })
        {
            await using var node = await Node.StartAsync(true,
                Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
                new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest",
                    "-testcoinbasematuritylong=30:134:140" }, 31);
            await using var db = await BitcoinBlake2bLedgerProbe.CreateAsync(fullSchema: true);
            var mapper = AutoMapperFactory.CreateMapper();
            var cf = db.Factory;
            var blocks = new BlockRepository(mapper);
            var shares = new ShareRepository(mapper);
            var balances = new BalanceRepository(mapper);
            var messages = Substitute.For<IMessageBus>();
            var config = new PoolConfig
            {
                Id = "blake2b-persist-" + payoutScheme, Coin = "bitcoin-blake2b", Template = Coin(30, 134, 140),
                Address = await node.GetNewAddressAsync(), Daemons = new[] { node.WalletEndpoint },
                RewardRecipients = Array.Empty<RewardRecipient>(),
                Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 1 },
                PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = payoutScheme },
            };
            var cluster = new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig() };
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(config);
            pool.NetworkStats.Returns(new BlockchainStats { BlockHeight = 31 });
            var contracts = new BitcoinBlake2bPayoutContractTracker();
            var grace = new ActiveBlockGracePeriodTracker();
            async Task<BitcoinBlake2bPayoutHandler> NewHandler()
            {
                var result = new BitcoinBlake2bPayoutHandler(container, cf, mapper, shares, blocks, balances,
                    new PaymentRepository(mapper), new StandardClock(), messages, grace, contracts);
                await result.ConfigureAsync(cluster, config, CancellationToken.None);
                return result;
            }
            PayoutManager NewManager() => new(container, cf, blocks, shares, balances, cluster, messages,
                Substitute.For<IPayoutManagerLease>(), new ProcessStatus());
            var handler = await NewHandler();
            var manager = NewManager();
            IPayoutScheme scheme = payoutScheme switch
            {
                PayoutScheme.SOLO => new SOLOPaymentScheme(shares, balances),
                PayoutScheme.PROP => new PROPPaymentScheme(cf, shares, blocks, balances),
                PayoutScheme.PPLNS => new PPLNSPaymentScheme(cf, shares, blocks, balances),
                _ => new PPSPaymentScheme(shares),
            };
            var reward = await Reward(node, 30);
            reward.PoolId = config.Id;
            reward.Miner = await node.GetNewAddressAsync();
            reward.NetworkDifficulty = 1;
            reward.Created = DateTime.UtcNow.AddSeconds(-1);
            reward.Effort = reward.MinerEffort = 1;
            Assert.True(await blocks.InsertAsync(db.Observer, null, reward));
            reward.Id = await db.Observer.ExecuteScalarAsync<long>("SELECT id FROM blocks WHERE poolid=@PoolId", reward);
            if(payoutScheme == PayoutScheme.PPS)
                await balances.AddAmountAsync(db.Observer, null, config.Id, reward.Miner, 7m, "Previously booked PPS liability");
            else
                await shares.BatchInsertAsync(db.Observer, null, new[] { new Miningcore.Persistence.Model.Share
                {
                    PoolId = config.Id, BlockHeight = reward.BlockHeight, Miner = reward.Miner,
                    Difficulty = 2, NetworkDifficulty = 1, IpAddress = "127.0.0.1", Created = reward.Created,
                } }, CancellationToken.None);
            Task Cycle() => manager.UpdatePoolBalancesAsync(pool, config, handler, scheme, CancellationToken.None);
            async Task<RewardBlock> Stored() => await cf.RunTx((con, tx) => blocks.GetBlockByIdForUpdateAsync(con, tx, reward.Id));
            await Cycle();
            Assert.Equal(BlockStatus.Pending, (await Stored()).Status);
            await node.RootRpcAsync("invalidateblock", reward.Hash);
            await Cycle();
            Assert.Equal(BlockStatus.Orphaned, (await Stored()).Status);
            Assert.Equal(0, (await Stored()).Reward);
            Assert.Empty(await blocks.GetPendingBlocksForPoolAsync(db.Observer, config.Id));
            await node.RootRpcAsync("reconsiderblock", reward.Hash);
            // Restart manager/handler; the database row remains Orphaned. No
            // test assignment or SQL reset of status participates in recovery.
            manager = NewManager();
            handler = await NewHandler();
            await Cycle();
            Assert.Equal(BlockStatus.Pending, (await Stored()).Status);
            Assert.Equal(50m, (await Stored()).Reward);
            await node.RootRpcAsync("invalidateblock", reward.Hash);
            await Cycle();
            Assert.Equal(BlockStatus.Orphaned, (await Stored()).Status);
            await node.RootRpcAsync("reconsiderblock", reward.Hash);
            await node.GenerateAsync(109, CancellationToken.None); // height140, 111 confirmations
            pool.NetworkStats.Returns(new BlockchainStats { BlockHeight = 140 });
            var stale = Assert.Single(await manager.LoadBlocksForClassificationAsync(pool, CancellationToken.None));
            await handler.ClassifyBlocksAsync(pool, new[] { stale }, CancellationToken.None);
            Assert.Equal(BlockStatus.Confirmed, stale.Status);
            await Cycle();
            Assert.Equal(BlockStatus.Confirmed, (await Stored()).Status);
            Assert.Equal(50m, (await Stored()).Reward);
            var expected = payoutScheme == PayoutScheme.PPS ? 7m : 50m;
            Assert.Equal(expected, await balances.GetBalanceAsync(db.Observer, config.Id, reward.Miner));
            var changes = await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes");
            // Replayed/concurrent stale classification loses under the row lock.
            await manager.RunBlockUpdateTransactionAsync(config, stale, (con, tx) =>
                manager.ApplyConfirmedBlockAsync(con, tx, pool, stale, handler, scheme, CancellationToken.None));
            await Cycle();
            manager = NewManager();
            handler = await NewHandler();
            await Cycle();
            Assert.Equal(expected, await balances.GetBalanceAsync(db.Observer, config.Id, reward.Miner));
            Assert.Equal(changes, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes"));
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
        }
    }

    [BitcoinBlake2bLedgerIntegrationFact]
    public async Task HistoricalRecovery_AfterLaterPropOrPplnsSettlementWithPrunedShares_HoldsAllocationAcrossRestart()
    {
        foreach(var payoutScheme in new[] { PayoutScheme.PROP, PayoutScheme.PPLNS })
        foreach(var status in new[] { BlockStatus.Orphaned, BlockStatus.Pending })
        {
            await using var node = await Node.StartAsync(true,
                Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable),
                new[] { "-testactivationheight=blake2b@20", "-blake2b_headline=Miningcore BLAKE2b regtest",
                    "-testcoinbasematuritylong=30:134:140" }, 141);
            await using var db = await BitcoinBlake2bLedgerProbe.CreateAsync(fullSchema: true);
            var mapper = AutoMapperFactory.CreateMapper();
            var blocks = new BlockRepository(mapper);
            var shares = new ShareRepository(mapper);
            var balances = new BalanceRepository(mapper);
            var messages = Substitute.For<IMessageBus>();
            var config = new PoolConfig
            {
                Id = "blake2b-history-" + payoutScheme, Coin = "bitcoin-blake2b", Template = Coin(30, 134, 140),
                Address = await node.GetNewAddressAsync(), Daemons = new[] { node.WalletEndpoint },
                RewardRecipients = Array.Empty<RewardRecipient>(),
                Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 1 },
                PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, PayoutScheme = payoutScheme },
            };
            var cluster = new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig() };
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(config);
            pool.NetworkStats.Returns(new BlockchainStats { BlockHeight = 141 });
            var grace = new ActiveBlockGracePeriodTracker();
            var contracts = new BitcoinBlake2bPayoutContractTracker();
            async Task<BitcoinBlake2bPayoutHandler> NewHandler()
            {
                var value = new BitcoinBlake2bPayoutHandler(container, db.Factory, mapper, shares, blocks, balances,
                    new PaymentRepository(mapper), new StandardClock(), messages, grace, contracts);
                await value.ConfigureAsync(cluster, config, CancellationToken.None);
                return value;
            }
            PayoutManager NewManager() => new(container, db.Factory, blocks, shares, balances, cluster,
                messages, Substitute.For<IPayoutManagerLease>(), new ProcessStatus());
            IPayoutScheme scheme = payoutScheme == PayoutScheme.PROP
                ? new PROPPaymentScheme(db.Factory, shares, blocks, balances)
                : new PPLNSPaymentScheme(db.Factory, shares, blocks, balances);
            var older = await Reward(node, 30);
            older.PoolId = config.Id;
            older.Miner = await node.GetNewAddressAsync();
            older.Created = DateTime.UtcNow.AddMinutes(-2);
            older.Status = status; // Historical dev-build ledger, before this recovery runs.
            older.Reward = status == BlockStatus.Orphaned ? 0 : 50;
            older.NetworkDifficulty = 1;
            older.Effort = older.MinerEffort = 1;
            var later = await Reward(node, 31);
            later.PoolId = config.Id;
            later.Miner = await node.GetNewAddressAsync();
            later.Created = older.Created.AddMinutes(1);
            later.NetworkDifficulty = 1;
            later.Effort = later.MinerEffort = 1;
            foreach(var block in new[] { older, later })
            {
                Assert.True(await blocks.InsertAsync(db.Observer, null, block));
                block.Id = await db.Observer.ExecuteScalarAsync<long>("SELECT id FROM blocks WHERE poolid=@PoolId AND hash=@Hash", block);
            }
            // Use persisted timestamp precision, as the production repository
            // load does, before the immutable row-lock classification check.
            older = await db.Factory.RunTx((con, tx) => blocks.GetBlockByIdForUpdateAsync(con, tx, older.Id));
            later = await db.Factory.RunTx((con, tx) => blocks.GetBlockByIdForUpdateAsync(con, tx, later.Id));
            await shares.BatchInsertAsync(db.Observer, null, new[] { older, later }.Select(block =>
                new Miningcore.Persistence.Model.Share
                {
                    PoolId = config.Id, BlockHeight = block.BlockHeight, Miner = block.Miner,
                    Difficulty = 2, NetworkDifficulty = 1, IpAddress = "127.0.0.1", Created = block.Created,
                }).ToArray(), CancellationToken.None);
            var handler = await NewHandler();
            var manager = NewManager();
            // Simulate the old processor settling B while omitting A from its
            // pending-only load, using the actual scheme and transaction path.
            await handler.ClassifyBlocksAsync(pool, new[] { later }, CancellationToken.None);
            Assert.Equal(BlockStatus.Confirmed, later.Status);
            await manager.RunBlockUpdateTransactionAsync(config, later, (con, tx) =>
                manager.ApplyConfirmedBlockAsync(con, tx, pool, later, handler, scheme, CancellationToken.None));
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares WHERE poolid=@PoolId AND created<=@Created", older));
            var credited = await db.Observer.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances");
            Assert.Equal(50m, credited);
            var changes = await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes");
            async Task AssertHeld()
            {
                await manager.UpdatePoolBalancesAsync(pool, config, handler, scheme, CancellationToken.None);
                var stored = await db.Factory.RunTx((con, tx) => blocks.GetBlockByIdForUpdateAsync(con, tx, older.Id));
                Assert.Equal(BlockStatus.Quarantined, stored.Status);
                Assert.Equal(older.Reward, stored.Reward);
                Assert.Equal(1, stored.ConfirmationProgress);
                Assert.Equal(older.Effort, stored.Effort);
                Assert.Equal(older.MinerEffort, stored.MinerEffort);
                Assert.Empty(await manager.LoadBlocksForClassificationAsync(pool, CancellationToken.None));
                Assert.Equal(credited, await db.Observer.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
                Assert.Equal(changes, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes"));
            }
            await AssertHeld();
            await AssertHeld();
            manager = NewManager();
            grace = new ActiveBlockGracePeriodTracker();
            contracts = new BitcoinBlake2bPayoutContractTracker();
            handler = await NewHandler();
            await AssertHeld();
            var alerts = messages.ReceivedCalls().Select(x => x.GetArguments()[0]).OfType<AdminNotification>()
                .Where(x => x.Subject.Contains("allocation recovery", StringComparison.Ordinal));
            Assert.Single(alerts);
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
            Assert.Equal(0, await db.Observer.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
        }
    }

    [BitcoinBlake2bLedgerIntegrationFact]
    public async Task AllocationHistoryQuery_ScopesPoolStatusTypeAndTimeWithoutOptionalDirectSchema()
    {
        await using var db = await BitcoinBlake2bLedgerProbe.CreateAsync();
        await db.Observer.ExecuteAsync(@"CREATE TABLE blocks(id bigint PRIMARY KEY,
            poolid text, status text, type text, created timestamptz);
            INSERT INTO blocks VALUES
            (1, 'review', 'confirmed', NULL, '2026-01-02T00:00:00Z'),
            (2, 'review', 'confirmed', NULL, '2026-01-01T00:00:00Z'),
            (3, 'other', 'confirmed', NULL, '2026-01-03T00:00:00Z'),
            (4, 'review', 'confirmed', 'auxpow', '2026-01-03T00:00:00Z'),
            (5, 'review', 'orphaned', NULL, '2026-01-03T00:00:00Z'),
            (6, 'review', 'pending', 'block', '2026-01-03T00:00:00Z'),
            (7, 'review', 'confirmed', 'bitcoin-coinbase-direct', '2026-01-03T00:00:00Z')");
        var repository = new BlockRepository(AutoMapperFactory.CreateMapper());
        var created = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Task<bool> Read() => db.Factory.RunTx((con, tx) =>
            repository.HasLaterConfirmedCustodialBlockAsync(con, tx, "review", created, 1));
        Assert.False(await Read());
        await db.Observer.ExecuteAsync("INSERT INTO blocks VALUES (8, 'review', 'confirmed', NULL, '2026-01-02T00:00:00Z')");
        Assert.True(await Read()); // Equal timestamps cannot establish allocation order.
        await db.Observer.ExecuteAsync("DELETE FROM blocks WHERE id=8; INSERT INTO blocks VALUES (9, 'review', 'confirmed', 'block', '2026-01-03T00:00:00Z')");
        Assert.True(await Read());
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
