using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Dapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Rpc;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using Xunit;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;

namespace Miningcore.Tests.Payments;

[Collection(PostgresPolicyCollection.Name)]
public class BitcoinCustodialSettlementIntegrationTests(IsolatedPostgresServer server) : TestBase
{
    [IsolatedPostgresTheory]
    [InlineData("litecoin", "merged-parent", 102, 101)]
    [InlineData("dogecoin", "auxpow", 251, 61)]
    [InlineData("bitcoin", "block", 102, 101)]
    [InlineData("nexa", "block", 102, 101)]
    public async Task RealManager_ThresholdRollbackRestartAndReplay_CreateExactlyOneSettlement(
        string coin, string type, int threshold, int walletMaturity)
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, 10);
        var mapper = container.Resolve<IMapper>();
        var blocks = new BlockRepository(mapper);
        var shares = new ShareRepository(mapper);
        var balances = new BalanceRepository(mapper);
        var payments = new PaymentRepository(mapper);
        var bus = Substitute.For<IMessageBus>();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var config = new PoolConfig
        {
            Id = coin, Template = ModuleInitializer.CoinTemplates[coin],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } },
            Extra = new Dictionary<string, object> { ["minimumConfirmations"] = threshold },
            RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new PoolPaymentProcessingConfig
            { Enabled = true, PayoutScheme = PayoutScheme.SOLO, MinimumPayment = 1m },
        };
        var cluster = new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig() };
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        var scheme = new SOLOPaymentScheme(shares, balances);
        var block = new Block
        {
            PoolId = coin, Type = type, Hash = "block-hash", BlockHeight = 100,
            TransactionConfirmationData = "coinbase", Status = BlockStatus.Pending,
            Reward = 25m, Miner = "finder", Created = DateTime.UtcNow.AddHours(-1),
            Effort = 1, MinerEffort = 1, NetworkDifficulty = 1,
        };
        await db.Factory.RunTx((con, tx) => blocks.InsertAsync(con, tx, block));
        var state = new WalletState { Confirmations = walletMaturity };
        var handler = await Handler();
        PayoutManager Manager() => new(container, db.Factory, blocks, shares, balances,
            cluster, bus, Substitute.For<IPayoutManagerLease>(), new ProcessStatus());
        async Task<FixtureHandler> Handler()
        {
            var value = new FixtureHandler(container, db, mapper, shares, blocks,
                balances, payments, clock, bus, state);
            await value.ConfigureAsync(cluster, config, CancellationToken.None);
            return value;
        }
        async Task Cycle()
        {
            var manager = Manager();
            await manager.UpdatePoolBalancesAsync(pool, config, handler, scheme, CancellationToken.None);
            await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        }
        async Task AssertUnsettled()
        {
            Assert.Equal("pending", await db.Observer.ExecuteScalarAsync<string>("SELECT status FROM blocks"));
            Assert.Equal(0, await db.Count("balances"));
            Assert.Equal(0, await db.Count("balance_changes"));
            Assert.Equal(0, await db.Count("payments"));
            Assert.Equal(0, await db.Count("payment_batches"));
            Assert.Equal(0, state.Submissions);
            bus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(), Arg.Any<string>());
        }

        // Wallet-mature rewards must survive repeated processing/restarts without liability.
        await Cycle();
        handler = await Handler();
        await Cycle();
        await AssertUnsettled();
        Assert.Equal(50m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT reward FROM blocks"));
        Assert.Equal("coinbase", await db.Observer.ExecuteScalarAsync<string>("SELECT transactionconfirmationdata FROM blocks"));

        state.Confirmations = threshold - 1;
        await Cycle();
        await AssertUnsettled();
        state.Confirmations = threshold;
        state.Category = "immature";
        await Cycle();
        await AssertUnsettled();
        state.Category = "generate";
        state.MissingConfirmations = true;
        await Cycle();
        await AssertUnsettled();
        state.MissingConfirmations = false;
        state.WalletError = -5; // independently active block, temporary wallet-index lag
        await Cycle();
        await AssertUnsettled();
        state.WalletError = -500;
        await Cycle();
        await AssertUnsettled();
        state.WalletError = null;
        state.Active = false; // wallet/chain disagreement must not settle
        await Cycle();
        await AssertUnsettled();
        state.Active = true;

        // Force a database failure after the terminal block update, during credit.
        await db.Observer.ExecuteAsync("""
            CREATE FUNCTION reject_credit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected credit failure'; END $$;
            CREATE TRIGGER reject_credit BEFORE INSERT ON balances
                FOR EACH ROW EXECUTE FUNCTION reject_credit();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => Cycle());
        await AssertUnsettled();
        await db.Observer.ExecuteAsync("DROP TRIGGER reject_credit ON balances");
        handler = await Handler();
        // Retain a stale classified observation to test row-lock replay after commit.
        var stale = await db.Factory.Run(con => blocks.GetPendingBlocksForPoolAsync(con, coin));
        await handler.ClassifyBlocksAsync(pool, stale, CancellationToken.None);
        await Cycle();
        state.Confirmations++;
        handler = await Handler();
        await Cycle();
        var replayManager = Manager();
        await replayManager.RunBlockUpdateTransactionAsync(config, Assert.Single(stale),
            (con, tx) => replayManager.ApplyConfirmedBlockAsync(con, tx, pool, stale[0], handler, scheme, CancellationToken.None));
        // A later orphan observation cannot replay or reverse an already settled liability.
        state.Active = false;
        state.Category = "orphan";
        await Cycle();

        Assert.Equal("confirmed", await db.Observer.ExecuteScalarAsync<string>("SELECT status FROM blocks"));
        Assert.Equal(type, await db.Observer.ExecuteScalarAsync<string>("SELECT type FROM blocks"));
        Assert.Equal(1, state.Submissions);
        Assert.Equal(1, await db.Count("payment_batches"));
        Assert.Equal(1, await db.Count("payments"));
        Assert.Equal(50m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM payments"));
        Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(2, await db.Count("balance_changes")); // one credit, one payment debit
        bus.Received(1).SendMessage(Arg.Any<BlockUnlockedNotification>(), Arg.Any<string>());
        await db.AssertUsable();
    }

    private sealed class WalletState
    {
        public int Confirmations;
        public string Category = "generate";
        public bool MissingConfirmations;
        public int? WalletError;
        public bool Active = true;
        public int Submissions;
    }

    private sealed class FixtureHandler : BitcoinPayoutHandler
    {
        private readonly WalletState state;
        public FixtureHandler(IComponentContext context, PostgresPersistenceTestDatabase db,
            IMapper mapper, ShareRepository shares, BlockRepository blocks,
            BalanceRepository balances, PaymentRepository payments, IMasterClock clock,
            IMessageBus bus, WalletState state) : base(context, db.Factory, mapper,
                shares, blocks, balances, payments, clock, bus, new ActiveBlockGracePeriodTracker())
        { this.state = state; }

        protected override Task<RpcResponse<JToken>[]> GetTransactionsAsync(Block[] blocks, CancellationToken ct) =>
            Task.FromResult(blocks.Select(block =>
            {
                var wallet = JObject.FromObject(new
                {
                    txid = block.TransactionConfirmationData, blockhash = block.Hash,
                    generated = true, blockindex = 0, amount = 50m,
                    details = new[] { new { category = state.Category } },
                });
                if(!state.MissingConfirmations) wallet["confirmations"] = state.Confirmations;
                return new RpcResponse<JToken>(wallet, state.WalletError.HasValue
                    ? new JsonRpcError(state.WalletError.Value, "injected wallet failure", null) : null);
            }).ToArray());

        protected override Task<RpcResponse<DaemonBlock>> GetBlockAsync(string hash, CancellationToken ct) =>
            Task.FromResult(new RpcResponse<DaemonBlock>(new DaemonBlock
            {
                Hash = hash, Height = 100, Confirmations = state.Active ? state.Confirmations : -1,
                Transactions = new[] { "coinbase" },
            }));

        protected override Task<RpcResponse<string>> SendManyAsync(object[] args, CancellationToken ct)
        {
            state.Submissions++;
            return Task.FromResult(new RpcResponse<string>("payment-tx"));
        }
        protected override Task<RpcResponse<JToken>> GetMempoolEntryAsync(string txid, CancellationToken ct) =>
            Task.FromResult(new RpcResponse<JToken>(new JObject()));
    }
}
