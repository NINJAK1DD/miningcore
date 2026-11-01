using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Miningcore.Blockchain.Pearl;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Tests.Persistence.Postgres;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Tests.Blockchain.Pearl;

// Actual Pearl receipt store, shared schemes, guarded settlement, and payout handler
// against PostgreSQL. Only node/wallet RPC is simulated here; live chain evidence is separate.
public class PearlRewardSchemeIntegrationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static string Address(byte value) => Encoders.Bech32("rprl").Encode(1, Enumerable.Repeat(value, 32).ToArray());

    private static Share Share(PoolConfig pool, string miner, double difficulty, DateTime created) => new()
    {
        PoolId = pool.Id, Miner = miner, Worker = "reward-test", IpAddress = "127.0.0.1", SessionId = "test",
        AccountingId = ShareAccounting.CreateId(), AccountingRole = Miningcore.Blockchain.ShareAccountingRole.Single,
        BlockHeight = 1, Created = created, PreserveCreated = true, Difficulty = difficulty,
        NetworkDifficulty = 100, ShareDifficulty = 10000, ActualDifficulty = 10000,
        RewardBasisSatoshis = 1_000_000_000, BlockReward = 10
    };

    private static async Task Commit(PearlCandidateStore store, PoolConfig pool, Share share, bool candidate = false, byte[] proof = null)
    {
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        await store.CommitAsync(share, share.AccountingId + share.AccountingId,
            new PearlWorkerContext {Difficulty = 1}.Issue(PearlWorkTests.Block(), 2), proof ?? new byte[] {1}, candidate, Ct);
    }

    [PostgresIntegrationFact]
    public async Task AllFourSchemesAllocateFeesAndPayExactlyOnceWithTheirDistinctWorkRules()
    {
        foreach(var kind in new[] {PayoutScheme.PPS, PayoutScheme.SOLO, PayoutScheme.PPLNS, PayoutScheme.PROP})
        {
            await using var lab = await PearlPayoutIntegrationTests.Lab.Create(true);
            var config = lab.Config;
            config.PaymentProcessing.PayoutScheme = kind;
            config.PaymentProcessing.PayoutSchemeConfig = JObject.FromObject(new {factor = 1m});
            config.RewardRecipients = new[] {new RewardRecipient {Address = Address(4), Percentage = 2.5m}};
            var mapper = AutoMapperFactory.CreateMapper();
            var shares = new ShareRepository(mapper); var blocks = new BlockRepository(mapper); var balances = new BalanceRepository(mapper);
            var store = new PearlCandidateStore(lab.Connections, shares, config);
            var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var finder = Share(config, Address(1), 25, time);
            var earlier = Share(config, Address(2), 100, time.AddSeconds(-1));
            var future = Share(config, Address(3), 100, time.AddSeconds(1));
            foreach(var share in new[] {earlier, finder, future}) await Commit(store, config, share);
            Assert.Equal(kind == PayoutScheme.PPS ? 3 : 0, await lab.Count("pps_share_credits"));
            var before = await lab.Db.ExecuteScalarAsync<decimal>("SELECT COALESCE(sum(amount),0) FROM balances");
            Assert.Equal(kind == PayoutScheme.PPS ? 21.9375m : 0m, before);

            var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config);
            var handler = await lab.Handler();
            IPayoutScheme scheme = kind switch
            {
                PayoutScheme.PPS => new PPSPaymentScheme(shares),
                PayoutScheme.SOLO => new SOLOPaymentScheme(shares, balances),
                PayoutScheme.PPLNS => new PPLNSPaymentScheme(lab.Connections, shares, blocks, balances),
                _ => new PROPPaymentScheme(lab.Connections, shares, blocks, balances)
            };
            var manager = new PayoutManager(Substitute.For<ILifetimeScope>(), lab.Connections, blocks, shares, balances,
                new ClusterConfig {PaymentProcessing = new ClusterPaymentProcessingConfig {Enabled = true}},
                Substitute.For<IMessageBus>(), Substitute.For<IPayoutManagerLease>(), Substitute.For<IProcessStatus>());
            var block = new Block {PoolId = config.Id, BlockHeight = 1, Miner = finder.Miner, Created = time,
                NetworkDifficulty = 100, Reward = 10, Hash = new string('a',64), TransactionConfirmationData = new string('b',64), Status = BlockStatus.Pending};
            await lab.Connections.RunTx((con, tx) => blocks.InsertAsync(con, tx, block, Ct));
            block.Id = await lab.Db.ExecuteScalarAsync<long>("SELECT id FROM blocks");

            // An orphan earns no block reward; PPS liabilities survive it unchanged.
            block.Status = BlockStatus.Orphaned;
            await manager.RunBlockUpdateTransactionAsync(config, block, (con, tx) => blocks.UpdateBlockAsync(con, tx, block));
            Assert.Equal(before, await lab.Db.ExecuteScalarAsync<decimal>("SELECT COALESCE(sum(amount),0) FROM balances"));
            block.Status = BlockStatus.Confirmed; block.PearlCustodialEvidenceVerified = true;
            Task Settle() => manager.RunBlockUpdateTransactionAsync(config, block,
                (con, tx) => manager.ApplyConfirmedBlockAsync(con, tx, pool, block, handler, scheme, Ct));
            await Task.WhenAll(Settle(), Settle());
            await Settle(); // restart/replay must not credit the reward or recipient twice
            async Task<decimal> Balance(string address) => await lab.Db.ExecuteScalarAsync<decimal>(
                "SELECT COALESCE((SELECT amount FROM balances WHERE address=@address),0)", new {address});
            Assert.Equal(kind switch {PayoutScheme.PROP => 1.95m, PayoutScheme.SOLO => 9.75m, _ => 2.4375m}, await Balance(finder.Miner));
            Assert.Equal(kind switch {PayoutScheme.PROP => 7.8m, PayoutScheme.SOLO => 0m, PayoutScheme.PPS => 9.75m, _ => 7.3125m}, await Balance(earlier.Miner));
            Assert.Equal(kind == PayoutScheme.PPS ? 9.75m : 0m, await Balance(future.Miner));
            Assert.Equal(.25m, await Balance(Address(4)));
            if(kind == PayoutScheme.PROP)
                Assert.Equal(1, await lab.Count("shares")); // inclusive disjoint round; future share survives
            if(kind == PayoutScheme.PPLNS)
                Assert.Equal(3, await lab.Count("shares")); // boundary retained for overlapping windows

            var finderBalance = await Balance(finder.Miner);
            var earlierBalance = await Balance(earlier.Miner);
            block = new Block {PoolId = config.Id, BlockHeight = 2, Miner = future.Miner, Created = future.Created,
                NetworkDifficulty = 100, Reward = 10, Hash = new string('c',64), TransactionConfirmationData = new string('d',64), Status = BlockStatus.Pending};
            await lab.Connections.RunTx((con, tx) => blocks.InsertAsync(con, tx, block, Ct));
            block.Id = await lab.Db.ExecuteScalarAsync<long>("SELECT max(id) FROM blocks");
            block.Status = BlockStatus.Confirmed; block.PearlCustodialEvidenceVerified = true;
            await Settle(); await Settle();
            Assert.Equal(finderBalance, await Balance(finder.Miner));
            Assert.Equal(earlierBalance, await Balance(earlier.Miner));
            Assert.Equal(9.75m, await Balance(future.Miner)); // PPS already paid for this work
            Assert.Equal(.5m, await Balance(Address(4)));
            if(kind == PayoutScheme.PROP) Assert.Equal(0, await lab.Count("shares"));
            if(kind == PayoutScheme.PPLNS) Assert.Equal(1, await lab.Count("shares"));

            var owed = (await lab.Db.QueryAsync<Balance>("SELECT * FROM balances WHERE amount>=0.01")).ToArray();
            await handler.PayoutAsync(pool, owed, Ct);
            Assert.Equal(1, lab.Server.Sends);
            // Shared persistence omits operator reward recipients from the miner
            // payment feed, but still debits their paid balance in the same batch.
            Assert.Equal(owed.Length - 1, await lab.Count("payments"));
            Assert.Equal(owed.Length + 1, ((JObject) lab.Server.SendArguments[1]).Count); // unique marker
            Assert.Equal(0m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
            Assert.Equal(0, await lab.Count("pearl_payout_intents"));
            await Settle();
            Assert.Equal(0m, await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));

            // A settled reward reorg holds subsequent payments, never reverses a paid ledger.
            block.Status = BlockStatus.Quarantined;
            await manager.RunBlockUpdateTransactionAsync(config, block, (con, tx) => blocks.UpdateBlockAsync(con, tx, block));
            await Assert.ThrowsAsync<InvalidDataException>(() => handler.PayoutAsync(pool, owed, Ct));
            Assert.Equal(1, lab.Server.Sends);
        }
    }

    [PostgresIntegrationFact]
    public async Task PpsReceiptReplayRemaindersAndCandidateFailuresAreAtomicForBothArithmeticVersions()
    {
        foreach(var version in new[] {0, 1})
        {
            await using var lab = await PearlPayoutIntegrationTests.Lab.Create(true);
            var config = lab.Config; config.PaymentProcessing.PayoutScheme = PayoutScheme.PPS;
            if(version == 1)
            {
                config.PaymentProcessing.PpsBinary64Activation = DateTime.UnixEpoch;
                await lab.Db.ExecuteAsync("SELECT activate_pps_binary64(@Id,@cutoff)", new {config.Id, cutoff = DateTime.UnixEpoch});
            }
            var shares = new ShareRepository(AutoMapperFactory.CreateMapper());
            var store = new PearlCandidateStore(lab.Connections, shares, config);
            var share = Share(config, Address(1), 1, DateTime.UtcNow);
            share.RewardBasisSatoshis = 1; share.NetworkDifficulty = 3;
            await Assert.ThrowsAnyAsync<Exception>(() => Commit(store, config, share, true, Array.Empty<byte>()));
            foreach(var table in new[] {"shares", "share_accounting_groups", "pps_share_credits", "pps_credit_remainders", "balances", "pearl_candidates"})
                Assert.Equal(0, await lab.Count(table));
            await Commit(store, config, share, true);
            await Commit(store, config, share, true);
            Assert.Equal(1, await lab.Count("pps_share_credits"));
            Assert.Equal(version, await lab.Db.ExecuteScalarAsync<int>("SELECT arithmeticversion FROM pps_share_credits"));

            // This is precisely the later ShareRecorder/relay recovery payload, not
            // a second candidate-store call. Serialization preserves the locked liability.
            var recovered = JsonConvert.DeserializeObject<Share>(JsonConvert.SerializeObject(share));
            var id = ShareAccounting.ParseCanonicalId(recovered.AccountingId);
            var projections = new[] {ShareAccounting.ToPersistenceShare(recovered)};
            var credits = new[] {ShareAccounting.CreatePpsCredit(config, recovered)};
            var batch = new ShareAccountingBatch {AccountingId = id, Shares = projections, PpsCredits = credits,
                Created = recovered.Created, PayloadHash = ShareAccounting.ComputePayloadHash(id, projections, credits)};
            var replay = await lab.Connections.RunTx((con, tx) => shares.InsertAccountingBatchAsync(con, tx, batch, Ct));
            Assert.NotEqual(ShareAccountingInsertResult.Inserted, replay);
            Assert.Equal(1, await lab.Count("pps_share_credits"));
            recovered.PpsCalculatedAmount *= 2;
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(recovered, recovered.AccountingId + recovered.AccountingId,
                new PearlWorkerContext {Difficulty = 1}.Issue(PearlWorkTests.Block(), 2), new byte[] {1}, false, Ct));
            for(var i = 0; i < 3; i++)
            {
                var next = Share(config, share.Miner, 1, share.Created.AddSeconds(i + 1));
                next.RewardBasisSatoshis = 1; next.NetworkDifficulty = 3;
                await Commit(store, config, next);
            }
            var total = await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(calculatedamount) FROM pps_share_credits");
            var balance = await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances");
            var remainder = await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM pps_credit_remainders");
            Assert.Equal(total, balance + remainder);
            Assert.True(balance > 0); Assert.True(remainder >= 0);

            for(var i = 0; i < 15; i++)
                await Commit(store, config, Share(config, share.Miner, 1, share.Created.AddSeconds(10 + i)), true);
            var creditCount = await lab.Count("pps_share_credits");
            var fullBalance = await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances");
            await Assert.ThrowsAsync<Miningcore.Stratum.StratumException>(() => Commit(store, config,
                Share(config, share.Miner, 1, share.Created.AddSeconds(30)), true));
            Assert.Equal(creditCount, await lab.Count("pps_share_credits"));
            Assert.Equal(fullBalance, await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
            // A rejected/obsolete block candidate cannot cancel an accepted PPS liability.
            var candidate = await store.NextAsync(config.Id, Ct);
            Assert.True(await store.CompleteAsync(candidate, null, new BlockRepository(AutoMapperFactory.CreateMapper()), Ct));
            Assert.Equal(fullBalance, await lab.Db.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        }
    }
}
