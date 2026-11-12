using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    [Theory]
    [InlineData("monero", typeof(CryptonotePayoutHandler), "transfer_split")]
    [InlineData("zano", typeof(ZanoPayoutHandler), "transfer_split")]
    [InlineData("conceal", typeof(ConcealPayoutHandler), "sendTransaction")]
    [InlineData("zcash", typeof(EquihashPayoutHandler), "z_shieldcoinbase")]
    [InlineData("xelis", typeof(XelisPayoutHandler), "set_online_mode")]
    public async Task ConfigurationNeverInvokesWalletMutation_ProbeFailuresRemainUncertain(
        string coin, Type handlerType, string mutation)
    {
        var calls = new ConcurrentQueue<string>();
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request.Value<string>("method");
            calls.Enqueue(method);
            JToken result = new JObject { ["chain"] = "main", ["network"] = "mainnet", ["nettype"] = "mainnet" };
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
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            handler.PayoutAsync(pool, Array.Empty<Balance>(), CancellationToken.None));
        Assert.Contains(mutation, calls);
    }
}
