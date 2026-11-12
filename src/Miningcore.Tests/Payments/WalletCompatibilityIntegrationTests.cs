using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;
using Miningcore.Rest;
using Miningcore.Tests.Rpc;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    private sealed class WalletCompatibilityTheoryAttribute : TheoryAttribute
    {
        public WalletCompatibilityTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_WALLET_MANIFEST")))
                Skip = "Requires the isolated, funded wallet compatibility lab and MININGCORE_TEST_WALLET_MANIFEST";
        }
    }

    [WalletCompatibilityTheory]
    [InlineData("monero", typeof(CryptonotePayoutHandler), false)]
    [InlineData("monero", typeof(CryptonotePayoutHandler), true)]
    [InlineData("conceal", typeof(ConcealPayoutHandler), false)]
    [InlineData("conceal", typeof(ConcealPayoutHandler), true)]
    [InlineData("zano", typeof(ZanoPayoutHandler), false)]
    [InlineData("zano", typeof(ZanoPayoutHandler), true)]
    [InlineData("zcash", typeof(EquihashPayoutHandler), false)]
    [InlineData("zcash", typeof(EquihashPayoutHandler), true)]
    [InlineData("xelis", typeof(XelisPayoutHandler), false)]
    [InlineData("xelis", typeof(XelisPayoutHandler), true)]
    public async Task LiveWalletCompatibility_PauseSuccessAndLostSubmissionPreserveAccounting(string coin, Type handlerType, bool cancel)
    {
        var manifest = JObject.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("MININGCORE_TEST_WALLET_MANIFEST")));
        var fixture = (JObject)manifest[coin];
        Assert.NotNull(fixture);
        await using var db = await Database.Create();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var methods = new ConcurrentQueue<string>();
        var responses = new ConcurrentQueue<string>();
        var loseResponse = false;
        var forwardedSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastSubmission = "";
        async Task Forward(HttpContext context, bool wallet)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var method = request.Value<string>("method") ?? context.Request.Path.Value;
            methods.Enqueue(method);
            var parameters = request["params"];
            var namedParameters = parameters as JObject;
            var submission = method switch
            {
                "transfer" or "transfer_split" => namedParameters?["destinations"] is JArray destinations && destinations.Count > 0,
                "sendTransaction" => namedParameters?["transfers"] is JArray transfers && transfers.Count > 0,
                "sendcurrency" => parameters is JArray send && send.Count > 1,
                "z_sendmany" => parameters is JArray args && args.Count > 1 && args.Last?.Value<string>() != "WrongPrivacyPolicy",
                "build_transaction" => namedParameters?["broadcast"]?.Value<bool>() == true,
                _ => false,
            };
            using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                $"http://127.0.0.1:{fixture.Value<int>(wallet ? "walletPort" : "daemonPort")}{context.Request.Path}{context.Request.QueryString}");
            if(fixture.Value<string>("user") is { } user)
                outgoing.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + fixture.Value<string>("password"))));
            if(body.Length != 0) outgoing.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(outgoing);
            var returned = await response.Content.ReadAsStringAsync();
            if(method is "get_balance" or "get_topoheight" or "is_online" or "z_getbalance" or "getBalance")
                responses.Enqueue(method + ": " + returned);
            // Monero labels its isolated regression chain 'fakechain', while the
            // production handler accepts named networks. Only this lab metadata
            // is normalized; wallet spending requests/responses stay unmodified.
            if(!wallet && coin == "monero" && returned.Contains("fakechain", StringComparison.Ordinal))
                returned = returned.Replace("\"fakechain\"", "\"mainnet\"", StringComparison.Ordinal);
            if(submission)
            {
                lastSubmission = returned;
                if(loseResponse)
                {
                    var envelope = JObject.Parse(returned);
                    Assert.True(envelope["error"] == null || envelope["error"].Type == JTokenType.Null,
                        "The real wallet must accept the submitted request before its response is withheld");
                    forwardedSubmission.TrySetResult();
                    if(cancel)
                    {
                        try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                        catch(OperationCanceledException) { }
                    }
                    context.Abort();
                    return;
                }
            }
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(returned);
        }
        await using var daemon = await RpcDiagnosticTests.Server.Start(context => Forward(context, false));
        await using var wallet = await RpcDiagnosticTests.Server.Start(context => Forward(context, true));
        var services = new ServiceCollection(); services.AddDaemonRestClient();
        using var provider = services.BuildServiceProvider();
        var mapper = container.Resolve<IMapper>();
        var shares = new ShareRepository(mapper); var blocks = new BlockRepository(mapper);
        var balances = new BalanceRepository(mapper); var payments = new PaymentRepository(mapper);
        var bus = new MessageBus(); var clock = new StandardClock();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(jsonSerializerSettings); builder.RegisterInstance(mapper);
        builder.RegisterInstance(db.Factory).As<IConnectionFactory>();
        builder.RegisterInstance(shares).As<IShareRepository>(); builder.RegisterInstance(blocks).As<IBlockRepository>();
        builder.RegisterInstance(balances).As<IBalanceRepository>(); builder.RegisterInstance(payments).As<IPaymentRepository>();
        builder.RegisterInstance(bus).As<IMessageBus>(); builder.RegisterInstance(clock).As<IMasterClock>();
        builder.RegisterInstance(provider.GetRequiredService<IHttpClientFactory>());
        builder.RegisterType<ActiveBlockGracePeriodTracker>().As<IActiveBlockGracePeriodTracker>();
        builder.RegisterType(handlerType).As<IPayoutHandler>();
        using var scope = builder.Build();
        static DaemonEndpointConfig Endpoint(RpcDiagnosticTests.Server server)
        {
            var endpoint = server.Endpoint();
            endpoint.HttpPath = null; endpoint.User = null; endpoint.Password = null;
            return endpoint;
        }
        var walletEndpoint = Endpoint(wallet); walletEndpoint.Category = "wallet";
        var config = new PoolConfig { Id = "live-" + coin, Coin = coin, Enabled = true,
            Address = fixture.Value<string>("address"), Template = ModuleInitializer.CoinTemplates[coin],
            Daemons = coin == "zcash" ? new[] { Endpoint(wallet) } : new[] { Endpoint(daemon), walletEndpoint },
            Extra = new Dictionary<string, object> { ["z-address"] = fixture.Value<string>("zAddress") ?? fixture.Value<string>("address") },
            RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new() { Enabled = true, MinimumPayment = .01m, PayoutScheme = PayoutScheme.SOLO } };
        var cluster = new ClusterConfig { PaymentProcessing = new() { Enabled = true }, Pools = new[] { config } };
        var handler = scope.Resolve<IPayoutHandler>();
        await handler.ConfigureAsync(cluster, config, CancellationToken.None);
        Assert.DoesNotContain(methods, method => method is "transfer_split" or "z_shieldcoinbase" or "sendcurrency" or "z_sendmany" or "set_online_mode");
        var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config);
        var recipient = fixture.Value<string>("recipient");
        await db.Factory.RunTx((connection, tx) => balances.AddAmountAsync(connection, tx, config.Id, recipient, .01m, "isolated wallet fixture"));
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        var manager = new PayoutManager(scope, db.Factory, blocks, shares, balances, cluster, bus, lease, new ProcessStatus());
        await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        Assert.Equal(0, await PaymentCount(db));
        Assert.Empty(lastSubmission);
        await db.SetPaused(false);
        for(var i = 0; i < 20 && await PaymentCount(db) == 0; i++)
        {
            await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
            if(await PaymentCount(db) == 0) await Task.Delay(1000);
        }
        Assert.True(await PaymentCount(db) == 1, $"No successful {coin} payment. Last wallet response: {lastSubmission}; reads: {string.Join("; ", responses.Distinct())}");
        Assert.Equal(0m, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        output.WriteLine($"{coin}: successful live wallet submission, one payment receipt, exact balance reset and completed admission.");
        await db.Factory.RunTx((connection, tx) => balances.AddAmountAsync(connection, tx, config.Id, recipient, .01m, "response-loss fixture"));
        loseResponse = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var payout = manager.PayoutPoolBalancesAsync(pool, config, handler, cancellation.Token);
        await forwardedSubmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(cancel) cancellation.Cancel();
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => payout);
        Assert.False(lease.CanReleaseOwnership);
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        Assert.Equal(1, await PaymentCount(db));
        Assert.Equal(.01m, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        await db.SetPaused(true);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.SetPaused(false));
        output.WriteLine($"{coin}: live spending request accepted, response {(cancel ? "cancelled" : "dropped")}; no automatic retry/reset, outstanding admission and ownership retained, resume rejected.");
    }
}
