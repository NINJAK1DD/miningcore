using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Payments;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;
using Node = Miningcore.Tests.Blockchain.Bitcoin.BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    private readonly ITestOutputHelper output;
    public WalletPayoutControlIntegrationTests(ITestOutputHelper output) => this.output = output;

    [WalletControlFact]
    public async Task MixedLocalPauseProcesses_ShareWalletAndDatabaseWithoutSharingOwnership()
    {
        await using var db = await Database.Create();
        await using var wallet = await Node.StartAsync(true);
        var cluster = TopologyConfig(wallet, await wallet.GetNewAddressAsync(), true);
        await using var paused = await TopologyProcess.Start(db, cluster);
        Assert.False((await paused.State())["processor"].Value<bool>());
        Assert.Contains("miningcore_payout_processor_paused 1", (await paused.State())["metrics"].Value<string>());
        Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_ownership WHERE owner_id IS NOT NULL"));
        await db.Admin.ExecuteAsync("INSERT INTO balances(poolid,address,amount,created,updated) VALUES(@pool,@address,1,now(),now())",
            new { pool = cluster.Pools[0].Id, address = cluster.Pools[0].Address });
        await db.SetPaused(false);
        cluster.PaymentProcessing.Paused = false;
        await using var active = await TopologyProcess.Start(db, cluster);
        var owner = await db.Admin.ExecuteScalarAsync<Guid>("SELECT owner_id FROM payout_manager_ownership");
        await using(var competitor = await TopologyProcess.Start(db, cluster, blocked: true))
            Assert.Equal(78, competitor.ExitCode);
        await Until(async () => await PaymentCount(db) == 1);
        await AssertCycles(active, 3);
        Assert.True((await active.State())["processor"].Value<bool>());
        Assert.False((await paused.State())["processor"].Value<bool>());
        Assert.Equal(0, (await paused.State())["cycles"].Value<int>());
        Assert.Equal(owner, await db.Admin.ExecuteScalarAsync<Guid>("SELECT owner_id FROM payout_manager_ownership"));
        await AssertWalletReceipts(db, wallet, 1);

        // A database pause applies to the still-unpaused spender, while local
        // pause has deliberately not stopped that other process's first payment.
        await db.SetPaused(true);
        await db.Admin.ExecuteAsync("UPDATE balances SET amount=1");
        var cycles = (await active.State())["cycles"].Value<int>();
        await AssertCycles(active, cycles + 3);
        Assert.Equal(1, await PaymentCount(db));
        Assert.Equal(1m, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        await paused.Stop();
        Assert.Equal(owner, await db.Admin.ExecuteScalarAsync<Guid>("SELECT owner_id FROM payout_manager_ownership"));
        await active.Stop();
        Assert.Null(await db.Admin.ExecuteScalarAsync<Guid?>("SELECT owner_id FROM payout_manager_ownership"));
        await using var restart = await TopologyProcess.Start(db, cluster);
        await AssertCycles(restart, 2);
        Assert.Equal(1, await PaymentCount(db));
        await db.SetPaused(false);
        await Until(async () => await PaymentCount(db) == 2);
        await AssertWalletReceipts(db, wallet, 2);
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        output.WriteLine("Three separate service processes shared one runtime login/database/wallet; local pause, global pause, exclusive ownership and restart verified.");
    }

    [WalletControlFact]
    public async Task LargePausedBacklog_RestartsBetweenMaturityWavesWithoutDuplicateAllocationOrPayment()
    {
        const int backlog = 256;
        await using var db = await Database.Create();
        await using var wallet = await Node.StartAsync(true, initialBlocks: 300);
        var cluster = TopologyConfig(wallet, await wallet.GetNewAddressAsync(), true);
        await using(var paused = await TopologyProcess.Start(db, cluster))
        {
            await SeedBlocks(db, wallet, cluster.Pools[0], backlog);
            Assert.Equal(backlog, await PendingCount(db));
            Assert.Equal(0, await PaymentCount(db));
            Assert.Equal(0, (await paused.State())["cycles"].Value<int>());
        }
        await db.SetPaused(false);
        cluster.PaymentProcessing.Paused = false;
        await using(var first = await TopologyProcess.Start(db, cluster))
        {
            await Until(async () => await PaymentCount(db) == 1);
            await AssertCycles(first, 3);
            Assert.InRange(await PendingCount(db), 1, backlog - 1);
            await AssertConservation(db);
        }
        var remaining = await PendingCount(db);
        var allocated = await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(reward) FROM blocks WHERE status='confirmed'");
        await using(var second = await TopologyProcess.Start(db, cluster))
        {
            await AssertCycles(second, 2);
            Assert.Equal(remaining, await PendingCount(db));
            Assert.Equal(1, await PaymentCount(db));
            Assert.Equal(allocated, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(reward) FROM blocks WHERE status='confirmed'"));
            await wallet.GenerateAsync(110, CancellationToken.None);
            await Until(async () => await PendingCount(db) == 0 && await PaymentCount(db) == 2);
            var cycles = (await second.State())["cycles"].Value<int>();
            await AssertCycles(second, cycles + 3);
            await AssertConservation(db);
        }
        await using(var third = await TopologyProcess.Start(db, cluster))
        {
            await AssertCycles(third, 3);
            Assert.Equal(backlog, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM blocks WHERE status='confirmed'"));
            Assert.Equal(2, await PaymentCount(db));
            await AssertConservation(db);
            await AssertWalletReceipts(db, wallet, 2);
            Assert.Equal(2, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
            Assert.Equal(0, (await db.State()).OutstandingBatches);
        }
        output.WriteLine($"{backlog} real pending Bitcoin blocks, two maturity/payment waves, three payout-process lifetimes; no missing or duplicate allocation, receipts or wallet transactions.");
    }

    private static ClusterConfig TopologyConfig(Node wallet, string address, bool paused) => new()
    {
        PaymentProcessing = new() { Enabled = true, Paused = paused, Interval = 1 },
        Pools = new[] { new PoolConfig
        {
            Id = "topology", Enabled = true, Coin = "bitcoin", Address = address,
            Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { wallet.WalletEndpoint }, RewardRecipients = Array.Empty<RewardRecipient>(),
            Extra = new Dictionary<string, object> { ["soloCoinbasePayout"] = false },
            PaymentProcessing = new() { Enabled = true, PayoutScheme = PayoutScheme.SOLO, MinimumPayment = 0.001m,
                Extra = new Dictionary<string, object> { ["minersPayTxFees"] = false } },
        } },
    };

    private static Task<int> PaymentCount(Database db) => db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payments");
    private static Task<int> PendingCount(Database db) => db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM blocks WHERE status='pending'");
    private static async Task Until(Func<Task<bool>> condition, int seconds = 180)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while(!await condition()) await Task.Delay(100, deadline.Token);
    }
    private static Task AssertCycles(TopologyProcess process, int count) =>
        Until(async () => (await process.State())["cycles"].Value<int>() >= count);

    private static async Task SeedBlocks(Database db, Node wallet, PoolConfig pool, int count)
    {
        var blocks = new BlockRepository(AutoMapperFactory.CreateMapper());
        for(var height = 1; height <= count; height++)
        {
            var hash = (await wallet.RootRpcAsync("getblockhash", height)).Value<string>();
            var block = await wallet.RootRpcAsync("getblock", hash);
            using var transaction = db.Admin.BeginTransaction();
            await blocks.InsertAsync(db.Admin, transaction, new Block
            {
                PoolId = pool.Id, Type = "block", Hash = hash, BlockHeight = (ulong) height,
                TransactionConfirmationData = block["tx"][0].Value<string>(), Status = BlockStatus.Pending,
                Miner = pool.Address, Created = DateTime.UtcNow.AddHours(-1),
                Effort = 1, MinerEffort = 1, NetworkDifficulty = 1,
            });
            transaction.Commit();
        }
    }
    private static async Task AssertConservation(Database db)
    {
        var rewards = await db.Admin.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(reward),0) FROM blocks WHERE status='confirmed'");
        var paid = await db.Admin.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(amount),0) FROM payments");
        var remaining = await db.Admin.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(amount),0) FROM balances");
        Assert.Equal(rewards, paid + remaining);
        Assert.Equal(remaining, await db.Admin.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(amount),0) FROM balance_changes"));
    }
    private static async Task AssertWalletReceipts(Database db, Node wallet, int count)
    {
        var receipts = (await db.Admin.QueryAsync<string>("SELECT transactionconfirmationdata FROM payments ORDER BY transactionconfirmationdata")).ToArray();
        Assert.Equal(count, receipts.Length);
        Assert.Equal(count, receipts.Distinct().Count());
        foreach(var receipt in receipts)
            Assert.Contains((await wallet.WalletRpcAsync("gettransaction", receipt))["details"], x => x["category"].Value<string>() == "send");
        var sends = (await wallet.WalletRpcAsync("listtransactions", "*", 1000)).Where(x => x["category"].Value<string>() == "send")
            .Select(x => x["txid"].Value<string>()).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(receipts, sends);
    }

    private sealed class TopologyProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;
        private bool stopped;
        private TopologyProcess(Process process) { this.process = process; errors = process.StandardError.ReadToEndAsync(); }
        public int ExitCode => process.ExitCode;
        public static async Task<TopologyProcess> Start(Database db, ClusterConfig cluster, bool blocked = false)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"));
            start.ArgumentList.Add("payout-topology-node");
            start.Environment["MININGCORE_TEST_POSTGRES"] = db.ConnectionString;
            start.Environment["MININGCORE_TEST_CLUSTER"] = JsonConvert.SerializeObject(cluster);
            var child = new TopologyProcess(Process.Start(start));
            try
            {
                Assert.Equal(blocked ? "blocked" : "ready", await child.Read());
                if(blocked) { await child.process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); child.stopped = true; }
                return child;
            }
            catch { await child.DisposeAsync(); throw; }
        }
        private async Task<string> Read()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while(await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                if(line.StartsWith("LAB ", StringComparison.Ordinal)) return line[4..];
            throw new InvalidOperationException("Lab node exited: " + await errors);
        }
        public async Task<JObject> State()
        {
            await process.StandardInput.WriteLineAsync("state");
            await process.StandardInput.FlushAsync();
            return JObject.Parse(await Read());
        }
        public async Task Stop()
        {
            if(stopped) return;
            await process.StandardInput.WriteLineAsync("stop");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            stopped = true;
            Assert.True(process.ExitCode == 0, await errors);
        }
        public async ValueTask DisposeAsync()
        {
            try { if(!process.HasExited) await Stop(); }
            finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
        }
    }
}
