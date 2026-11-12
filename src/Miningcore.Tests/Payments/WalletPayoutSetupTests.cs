using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rest;
using Miningcore.Tests.Rpc;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class WalletPayoutSetupTests : TestBase
{
    [Fact]
    public async Task EquihashCapabilities_SurviveHandlerRecreationButRetryInconclusiveProbeAndConfigChange()
    {
        var calls = new ConcurrentQueue<string>();
        var failPrivacy = true;
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request.Value<string>("method"); calls.Enqueue(method);
            JToken result = method switch
            {
                "getblockchaininfo" => new JObject { ["chain"] = "main" },
                "listunspent" => new JArray(),
                "z_getbalance" => new JValue(0.0),
                _ => JValue.CreateNull(),
            };
            JToken error = method switch
            {
                "z_shieldcoinbase" or "sendcurrency" => new JObject { ["code"] = -32601, ["message"] = "not supported" },
                "z_sendmany" => new JObject { ["code"] = failPrivacy ? -500 : -8, ["message"] = "probe rejected" },
                _ => JValue.CreateNull(),
            };
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = result, ["error"] = error }.ToString());
        });
        var config = new PoolConfig { Id = "capabilities", Coin = "zcash", Address = "t-address",
            Template = ModuleInitializer.CoinTemplates["zcash"], Daemons = new[] { server.Endpoint() },
            Extra = new Dictionary<string, object> { ["zAddress"] = "z-address" }, PaymentProcessing = new() };
        var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config);
        async Task RunCycle()
        {
            var handler = new EquihashPayoutHandler(container, Substitute.For<IConnectionFactory>(), container.Resolve<IMapper>(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
                Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>());
            await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
            await handler.PayoutAsync(pool, new[] { new Balance { Address = "recipient", Amount = 1m } }, CancellationToken.None);
        }
        await Assert.ThrowsAnyAsync<Exception>(RunCycle);
        failPrivacy = false;
        await RunCycle();
        await RunCycle();
        Assert.Equal(1, calls.Count(method => method == "z_shieldcoinbase"));
        Assert.Equal(1, calls.Count(method => method == "sendcurrency"));
        Assert.Equal(2, calls.Count(method => method == "z_sendmany"));
        config.Address = "changed-pool-wallet";
        await RunCycle();
        Assert.Equal(2, calls.Count(method => method == "z_shieldcoinbase"));
        Assert.Equal(2, calls.Count(method => method == "sendcurrency"));
        Assert.Equal(3, calls.Count(method => method == "z_sendmany"));
    }

    [Fact]
    public async Task XelisActivationDefersPayoutUntilLaterCycleAndWalletCatchup()
    {
        var online = false;
        var caughtUp = false;
        var missingHeight = false;
        var calls = new ConcurrentQueue<string>();
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request.Value<string>("method");
            calls.Enqueue(method);
            JToken result = method switch
            {
                "is_online" => new JValue(online),
                "set_online_mode" => new JValue(online = true),
                "get_topoheight" => new JValue(caughtUp ? 10 : 9),
                "get_balance" => new JValue(0),
                _ => new JObject { ["network"] = "mainnet", ["topoheight"] = missingHeight ? JValue.CreateNull() : new JValue(10), ["peer_count"] = 4 },
            };
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = result, ["error"] = null }.ToString());
        });
        var handler = new XelisPayoutHandler(container, Substitute.For<IConnectionFactory>(), container.Resolve<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>());
        var wallet = server.Endpoint(); wallet.Category = "wallet";
        var config = new PoolConfig { Id = "xelis-sync", Template = ModuleInitializer.CoinTemplates["xelis"],
            Daemons = new[] { server.Endpoint(), wallet }, Extra = new Dictionary<string, object>(), PaymentProcessing = new() };
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
        var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config);
        var balances = new[] { new Balance { Address = "test-recipient", Amount = 1m } };
        await handler.PayoutAsync(pool, balances, CancellationToken.None);
        Assert.Contains("set_online_mode", calls);
        Assert.DoesNotContain("get_balance", calls);
        await handler.PayoutAsync(pool, balances, CancellationToken.None);
        Assert.Contains("get_topoheight", calls);
        Assert.DoesNotContain("get_balance", calls);
        caughtUp = true;
        missingHeight = true;
        await handler.PayoutAsync(pool, balances, CancellationToken.None);
        Assert.DoesNotContain("get_balance", calls);
        missingHeight = false;
        await handler.PayoutAsync(pool, balances, CancellationToken.None);
        Assert.Contains("get_balance", calls);
        Assert.DoesNotContain("build_transaction", calls);
    }

    [Theory]
    [InlineData("monero", typeof(CryptonotePayoutHandler), "transfer_split")]
    [InlineData("zano", typeof(ZanoPayoutHandler), "transfer_split")]
    [InlineData("conceal", typeof(ConcealPayoutHandler), "sendTransaction")]
    [InlineData("zcash", typeof(EquihashPayoutHandler), "z_shieldcoinbase")]
    [InlineData("xelis", typeof(XelisPayoutHandler), "set_online_mode")]
    public async Task ConfigurationAndEmptyPayoutNeverInvokeWalletMutation(
        string coin, Type handlerType, string mutation)
    {
        var calls = new ConcurrentQueue<string>();
        var peers = 4;
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            if(context.Request.Method == "GET")
            {
                await context.Response.WriteAsJsonAsync(new { status = "OK", incoming_connections_count = peers, outgoing_connections_count = peers });
                return;
            }
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request.Value<string>("method");
            calls.Enqueue(method);
            JToken result = new JObject { ["chain"] = "main", ["network"] = "mainnet", ["nettype"] = "mainnet",
                ["incoming_connections_count"] = peers, ["outgoing_connections_count"] = peers, ["peer_count"] = peers };
            JToken error = JValue.CreateNull();
            if(method == "is_online") result = new JValue(false);
            if(method == mutation)
            {
                result = JValue.CreateNull();
                error = new JObject { ["code"] = -500, ["message"] = "lost response" };
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(new JObject
            { ["id"] = request["id"], ["result"] = result, ["error"] = error }.ToString(Formatting.None));
        });
        var services = new ServiceCollection();
        services.AddDaemonRestClient();
        using var provider = services.BuildServiceProvider();
        var httpFactory = provider.GetRequiredService<IHttpClientFactory>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(jsonSerializerSettings);
        builder.RegisterInstance(container.Resolve<IMapper>());
        builder.RegisterInstance(Substitute.For<IConnectionFactory>());
        builder.RegisterInstance(Substitute.For<IShareRepository>());
        builder.RegisterInstance(Substitute.For<IBlockRepository>());
        builder.RegisterInstance(Substitute.For<IBalanceRepository>());
        builder.RegisterInstance(Substitute.For<IPaymentRepository>());
        builder.RegisterInstance(Substitute.For<IMasterClock>());
        builder.RegisterInstance(Substitute.For<IMessageBus>());
        builder.RegisterInstance(httpFactory);
        builder.RegisterType<ActiveBlockGracePeriodTracker>().As<IActiveBlockGracePeriodTracker>();
        builder.RegisterType(handlerType).As<IPayoutHandler>();
        using var scope = builder.Build();
        var handler = scope.Resolve<IPayoutHandler>();
        var wallet = server.Endpoint();
        wallet.Category = "wallet";
        var config = new PoolConfig
        {
            Id = "setup", Template = ModuleInitializer.CoinTemplates[coin],
            Daemons = new[] { server.Endpoint(), wallet },
            Extra = new Dictionary<string, object>(), PaymentProcessing = new PoolPaymentProcessingConfig(),
        };
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
        Assert.DoesNotContain(mutation, calls);
        Assert.DoesNotContain("is_online", calls);
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        await handler.PayoutAsync(pool, Array.Empty<Balance>(), CancellationToken.None);
        Assert.DoesNotContain(mutation, calls);
#if !DEBUG
        peers = 0;
        if(coin != "zcash")
        {
            await handler.PayoutAsync(pool, new[] { new Balance { Address = "invalid-recipient", Amount = 1m } }, CancellationToken.None);
            Assert.DoesNotContain(mutation, calls);
        }
#endif
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyArgumentProbe_RealConnectionRefusalOrCancellationDoesNotRequireWalletReconciliation(bool cancel)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            arrived.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch(OperationCanceledException) { }
        });
        var endpoint = server.Endpoint();
        // Use a bound, non-listening socket to guarantee refusal without a port race.
        using var refused = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        refused.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        if(!cancel) endpoint.Port = ((System.Net.IPEndPoint) refused.LocalEndPoint).Port;
        var client = new Miningcore.Rpc.RpcClient(endpoint, jsonSerializerSettings, Substitute.For<IMessageBus>(), "probe-test");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = client.ExecuteAsync<JToken>(NLog.LogManager.GetCurrentClassLogger(), "z_shieldcoinbase", cancellation.Token);
        if(cancel)
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var result = await request;
        Assert.Equal(-500, result.Error.Code);
        Assert.Throws<InvalidOperationException>(() => WalletSubmissionOutcome.RequireRejectedProbe(
            result.Error, "z_shieldcoinbase", hasNoTransferArguments: true));
    }
}
