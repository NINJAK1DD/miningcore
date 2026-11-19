using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Equihash;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    [ZcashFragmentedFeeFact]
    public async Task LiveZcashFragmentedFunds_MultiplePagesRejectConclusiveFeeShortfallThenRetry()
    {
        var fixture = JObject.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("MININGCORE_TEST_WALLET_MANIFEST")))["zcash"];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            fixture.Value<string>("user") + ":" + fixture.Value<string>("password"))));
        async Task<JToken> Rpc(string method, params object[] args)
        {
            using var response = await http.PostAsync($"http://127.0.0.1:{fixture.Value<int>("walletPort")}/",
                new StringContent(new JObject { ["id"] = 1, ["method"] = method, ["params"] = JArray.FromObject(args) }.ToString(), Encoding.UTF8, "application/json"));
            var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(envelope["error"] == null || envelope["error"].Type == JTokenType.Null, method + ": " + envelope["error"]);
            return envelope["result"];
        }
        async Task Confirm(string txid)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            // Sapling's default anchor lags the tip by ten blocks; eleven
            // confirmations ensure the new note is included in that anchor.
            await Rpc("generate", 11);
            // Block generation returns before the wallet notification queue has
            // caught up. Poll confirmation state; never resubmit the operation.
            while((await Rpc("gettransaction", txid)).Value<int>("confirmations") < 10)
                await Task.Delay(200, deadline.Token);
        }
        async Task Complete(string operation)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string txid;
            while(true)
            {
                var result = (JArray) await Rpc("z_getoperationresult", new object[] { new[] { operation } });
                if(result.Count != 0)
                {
                    Assert.True(result[0].Value<string>("status") == "success", result[0].ToString());
                    txid = result[0]["result"].Value<string>("txid");
                    break;
                }
                await Task.Delay(200, deadline.Token);
            }
            await Confirm(txid);
        }
        var funding = fixture.Value<string>("zAddress");
        if((await Rpc("z_getbalance", funding, 1)).Value<decimal>() < 2m)
        {
            var shield = await Rpc("z_shieldcoinbase", fixture.Value<string>("address"), funding);
            await Complete(shield.Value<string>("opid"));
        }
        var source = (await Rpc("z_getnewaddress", "sapling")).Value<string>();
        async Task Fund(decimal amount)
        {
            var fundingNotes = await Rpc("z_listunspent", 1, 9999999, false, new[] { funding });
            Assert.True((await Rpc("z_getbalance", funding, 1)).Value<decimal>() >= amount + .001m, fundingNotes.ToString());
            var operation = await Rpc("z_sendmany", funding, new[] { new { address = source, amount } }, 1, null, "AllowRevealedAmounts");
            await Complete(operation.Value<string>());
        }
        for(var i = 0; i < 8; i++) await Fund(.1m);
        var notes = (JArray) await Rpc("z_listunspent", 1, 9999999, false, new[] { source });
        Assert.Equal(8, notes.Count);
        Assert.Equal(.8m, notes.Sum(x => x.Value<decimal>("amount")));

        await using var db = await Database.Create();
        var mapper = AutoMapperFactory.CreateMapper();
        var shares = new ShareRepository(mapper); var blocks = new BlockRepository(mapper);
        var balances = new BalanceRepository(mapper); var payments = new PaymentRepository(mapper);
        var bus = new MessageBus();
        var notices = new List<PaymentNotification>();
        using var observed = bus.Listen<PaymentNotification>().Subscribe(notices.Add);
        var config = new PoolConfig { Id = "fragmented-zcash", Coin = "zcash", Enabled = true,
            Address = (await Rpc("getnewaddress")).Value<string>(), Template = ModuleInitializer.CoinTemplates["zcash"],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = fixture.Value<int>("walletPort"),
                User = fixture.Value<string>("user"), Password = fixture.Value<string>("password") } },
            Extra = new Dictionary<string, object> { ["z-address"] = source }, RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new() { Enabled = true, MinimumPayment = .00001m, PayoutScheme = PayoutScheme.SOLO } };
        var cluster = new ClusterConfig { Pools = new[] { config }, PaymentProcessing = new() { Enabled = true } };
        var handler = new EquihashPayoutHandler(container, db.Factory, mapper, shares, blocks, balances, payments,
            new StandardClock(), bus, new ActiveBlockGracePeriodTracker());
        await handler.ConfigureAsync(cluster, config, CancellationToken.None);
        var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config);
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        var manager = new PayoutManager(container, db.Factory, blocks, shares, balances, cluster, bus, lease, new ProcessStatus());
        await db.SetPaused(false);
        async Task Add(int count, decimal total)
        {
            var each = decimal.Floor(total / count * 100_000_000m) / 100_000_000m;
            for(var i = 0; i < count; i++)
            {
                var recipient = (await Rpc("getnewaddress")).Value<string>();
                await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, config.Id, recipient,
                    i == count - 1 ? total - each * (count - 1) : each, "fragmented fee fixture"));
            }
        }
        await Add(50, .5m);
        await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        Assert.Equal(50, await PaymentCount(db));
        await Confirm(await db.Admin.ExecuteScalarAsync<string>("SELECT transactionconfirmationdata FROM payments LIMIT 1"));
        var available = (await Rpc("z_getbalance", source, 8)).Value<decimal>();
        notes = (JArray) await Rpc("z_listunspent", 8, 9999999, false, new[] { source });
        Assert.True(notes.Count > 1);
        // The first 50-recipient page passes our output/change estimate, but the
        // real wallet needs additional Sapling input actions. No response is forged.
        // Equal liabilities make the page total independent of database row order.
        var perRecipient = decimal.Floor((available - .00255m) / 50 * 100_000_000m) / 100_000_000m;
        await Add(51, perRecipient * 51);
        await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        var rejection = Assert.Single(notices.Where(x => x.Error?.Contains("insufficient", StringComparison.OrdinalIgnoreCase) == true ||
            x.Error?.Contains("fee", StringComparison.OrdinalIgnoreCase) == true));
        Assert.Equal(PaymentNotificationOutcome.Failure, rejection.Outcome);
        Assert.Equal(50, rejection.RecipientsCount);
        Assert.InRange(await PaymentCount(db), 50, 51);
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        Assert.True(lease.CanReleaseOwnership);
        var remaining = await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances");
        Assert.True(remaining > 0);
        var before = await PaymentCount(db);
        await Fund(1m);
        await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        Assert.Equal(101, await PaymentCount(db));
        Assert.Equal(0m, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        Assert.Equal(101, await db.Admin.ExecuteScalarAsync<int>("SELECT count(DISTINCT address) FROM payments"));
        output.WriteLine($"Real wallet rejection: {rejection.Error}");
        output.WriteLine($"Zcash: eight real shielded notes; paged fee rejection retained liability and completed admission; funding/retry grew payment receipts from {before} to 101 distinct recipients without replaying earlier payments.");
    }
}

public sealed class ZcashFragmentedFeeFactAttribute : FactAttribute
{
    public ZcashFragmentedFeeFactAttribute()
    {
        if(!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_WALLET_MANIFEST")))
            Skip = "Requires the isolated funded Zcash wallet compatibility lab";
    }
}
