using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Npgsql;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class MergedMiningPayoutRegtestTests
{
    private static async Task AssertResumedWalletLifecycleAsync(ClusterConfig cluster,
        PgConnectionFactory database, NpgsqlConnection connection, RegtestNode litecoin, RegtestNode dogecoin)
    {
        // Mature the real paused candidates past both networks' configured thresholds.
        await litecoin.RpcAsync("generatetoaddress", false, 260, cluster.Pools[0].Address);
        await dogecoin.RpcAsync("generate", true, 260);
        // Let every paired ordinary share settle before the first wallet cycle. Otherwise
        // legitimate staggered allocations can produce more than one payment per pool.
        var newest = await connection.ExecuteScalarAsync<DateTime>("SELECT max(created) FROM blocks");
        var settlementDelay = newest + PayoutManager.MergedParentShareSettlementDelay - DateTime.UtcNow;
        if(settlementDelay > TimeSpan.Zero)
            await Task.Delay(settlementDelay + TimeSpan.FromMilliseconds(100));
        var before = (await connection.QueryAsync<(string PoolId, decimal Amount)>(
            "SELECT poolid, sum(amount) FROM balances GROUP BY poolid")).ToDictionary(x => x.PoolId, x => x.Amount);
        var mapper = AutoMapperFactory.CreateMapper();
        var blocks = new BlockRepository(mapper);
        var shares = new ShareRepository(mapper);
        var balances = new BalanceRepository(mapper);
        var probe = new PauseWalletProbe();
        var builder = new ContainerBuilder();
        builder.Register(_ => new PauseWalletHandler(database, probe)).As<IPayoutHandler>()
            .WithMetadata(nameof(CoinFamilyAttribute.SupportedFamilies), new[] { CoinFamily.Bitcoin });
        builder.RegisterInstance(new SOLOPaymentScheme(shares, balances)).Keyed<IPayoutScheme>(PayoutScheme.SOLO);
        builder.RegisterInstance(new PPSPaymentScheme(shares)).Keyed<IPayoutScheme>(PayoutScheme.PPS);
        builder.RegisterInstance(new PROPPaymentScheme(database, shares, blocks, balances)).Keyed<IPayoutScheme>(PayoutScheme.PROP);
        await using var container = builder.Build();
        cluster.PaymentProcessing.Interval = 1;
        await using var lease = new PostgresPayoutManagerLease(database);
        var status = new ProcessStatus();
        using var manager = new PayoutManager(container, database, blocks, shares, balances,
            cluster, new MessageBus(), lease, status);
        foreach(var config in cluster.Pools)
        {
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(config);
            manager.AttachPool(pool);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await manager.StartAsync(deadline.Token);
        try
        {
            // Run the actual hosted loop, including its initial delay, ownership checks,
            // real handler resolution, classification, allocation, payments and retention.
            while(await connection.ExecuteScalarAsync<int>("SELECT count(DISTINCT poolid) FROM payments") != cluster.Pools.Length ||
                  cluster.Pools.Any(pool => !probe.Cycles.TryGetValue(pool.Id, out var count) || count < 4))
            {
                if(manager.ExecuteTask.IsCompleted) await manager.ExecuteTask;
                await Task.Delay(100, deadline.Token);
            }
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM blocks WHERE status NOT IN ('confirmed', 'orphaned')"));
            foreach(var pool in cluster.Pools)
            {
                Assert.True(await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM blocks WHERE poolid=@id AND status='confirmed'", new { id = pool.Id }) > 0);
                var reward = pool.PaymentProcessing.PayoutScheme == PayoutScheme.PPS ? 0 :
                    await connection.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(reward),0) FROM blocks WHERE poolid=@id AND status='confirmed'", new { id = pool.Id });
                var paid = await connection.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM payments WHERE poolid=@id", new { id = pool.Id });
                var remainder = await connection.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(amount),0) FROM balances WHERE poolid=@id", new { id = pool.Id });
                Assert.True(paid > 0);
                Assert.Equal(before.GetValueOrDefault(pool.Id) + reward, paid + remainder);
                Assert.Single(probe.Sends.Where(x => x.PoolId == pool.Id));
                var sent = probe.Sends.Single(x => x.PoolId == pool.Id);
                var node = pool.Coin == "litecoin" ? litecoin : dogecoin;
                var wallet = await node.RpcAsync("gettransaction", true, sent.TxId);
                Assert.NotEmpty(wallet["details"].Where(x => x["category"]?.Value<string>() == "send"));
                Assert.Equal(sent.TxId, await connection.ExecuteScalarAsync<string>(
                    "SELECT DISTINCT transactionconfirmationdata FROM payments WHERE poolid=@id", new { id = pool.Id }));
            }
            var paidCount = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payments");
            var sendCount = probe.Sends.Count;
            var creditCount = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits");

            // A further isolated liability simulates work received after the initial backlog.
            // Broadcast for real, then lose the successful reply at the RPC boundary.
            var target = cluster.Pools[0];
            probe.DropPool = target.Id;
            Assert.Equal(1, await connection.ExecuteAsync("UPDATE balances SET amount=amount+0.1 WHERE poolid=@id AND address=@address",
                new { id = target.Id, address = target.Address }));
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(async () => await manager.ExecuteTask.WaitAsync(deadline.Token));
            Assert.Equal(sendCount + 1, probe.Sends.Count);
            Assert.Equal(paidCount, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
            Assert.Equal(creditCount, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
            Assert.True(await connection.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances WHERE poolid=@id", new { id = target.Id }) >= 0.1m);
            Assert.Equal(ProcessExitCodes.GeneralFailure, status.ExitCode);
            Assert.NotNull(await litecoin.RpcAsync("gettransaction", true, probe.Sends.Last().TxId));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.StopAsync(stop.Token);
        }
        Assert.NotNull(await connection.ExecuteScalarAsync<Guid?>("SELECT owner_id FROM payout_manager_ownership WHERE id=1"));
        await using var replacementLease = new PostgresPayoutManagerLease(database);
        var replacementStatus = new ProcessStatus();
        using var replacement = new PayoutManager(container, database, blocks, shares, balances,
            cluster, new MessageBus(), replacementLease, replacementStatus);
        var attempts = probe.Sends.Count;
        await Assert.ThrowsAsync<PoolStartupException>(() => replacement.StartAsync(deadline.Token));
        Assert.Equal(ProcessExitCodes.PayoutOwnershipBlocked, replacementStatus.ExitCode);
        Assert.Equal(attempts, probe.Sends.Count);
    }

    private sealed class PauseWalletProbe
    {
        public ConcurrentDictionary<string, int> Cycles { get; } = new();
        public ConcurrentQueue<(string PoolId, string TxId)> Sends { get; } = new();
        public volatile string DropPool;
    }

    private sealed class PauseWalletHandler : BitcoinPayoutHandler
    {
        private readonly PauseWalletProbe probe;
        public PauseWalletHandler(PgConnectionFactory database, PauseWalletProbe probe) :
            base(ModuleInitializer.Container, database, AutoMapperFactory.CreateMapper(),
                new ShareRepository(AutoMapperFactory.CreateMapper()), new BlockRepository(AutoMapperFactory.CreateMapper()),
                new BalanceRepository(AutoMapperFactory.CreateMapper()), new PaymentRepository(AutoMapperFactory.CreateMapper()),
                new StandardClock(), new MessageBus(), new ActiveBlockGracePeriodTracker()) => this.probe = probe;

        public override async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
        {
            await base.ConfigureAsync(cc, pc, ct);
            probe.Cycles.AddOrUpdate(pc.Id, 1, (_, count) => count + 1);
        }

        protected override async Task<RpcResponse<string>> SendManyAsync(object[] args, CancellationToken ct)
        {
            var result = await base.SendManyAsync(args, ct);
            if(result.Error == null && !string.IsNullOrEmpty(result.Response))
            {
                probe.Sends.Enqueue((poolConfig.Id, result.Response));
                if(probe.DropPool == poolConfig.Id)
                {
                    probe.DropPool = null;
                    return new RpcResponse<string>(null, new JsonRpcError(
                        WalletSubmissionOutcome.TransportErrorCode, "Injected lost wallet reply", null));
                }
            }
            return result;
        }
    }
}
