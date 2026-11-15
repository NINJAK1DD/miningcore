using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Equihash;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Tests.Rpc;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class EquihashWalletResilienceTests : TestBase
{
    private async Task Cycle(PoolConfig config, int recipients = 1)
    {
        var handler = new EquihashPayoutHandler(container, Substitute.For<IConnectionFactory>(), container.Resolve<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(), Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>(),
            new ActiveBlockGracePeriodTracker());
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        await handler.PayoutAsync(pool, Enumerable.Range(0, recipients).Select(i =>
            new Balance { PoolId = config.Id, Address = "recipient-" + i, Amount = 1m }).ToArray(), CancellationToken.None);
    }

    private static PoolConfig Config(RpcDiagnosticTests.Server server) => new()
    {
        Id = "wallet-resilience", Coin = "zcash", Address = "t-address",
        Template = ModuleInitializer.CoinTemplates["zcash"], Daemons = new[] { server.Endpoint() },
        Extra = new Dictionary<string, object> { ["z-address"] = "z-address" }, PaymentProcessing = new(),
    };

    [Theory]
    [InlineData("sendcurrency")]
    [InlineData("z_shieldcoinbase")]
    [InlineData("privacy")]
    public async Task BackendReplacementReprobesOnlyAfterConclusiveRejectionAndOnNextCycle(string changed)
    {
        var generation = 0;
        var probes = 0;
        var submissions = new List<(int Generation, string Method, int Arguments)>();
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request.Value<string>("method");
            var args = request["params"] as JArray;
            var probe = (method is "sendcurrency" or "z_shieldcoinbase") && (args == null || args.Count == 0) ||
                method == "z_sendmany" && args?.Last?.Value<string>() == "WrongPrivacyPolicy";
            JToken result = method switch
            {
                "getblockchaininfo" => new JObject { ["chain"] = "main" },
                "listunspent" => new JArray(),
                "z_getbalance" => new JValue(100d),
                _ => JValue.CreateNull(),
            };
            int? code = null;
            if(probe)
            {
                probes++;
                code = method switch
                {
                    "sendcurrency" => changed == method && generation == 0 ? -1 : -32601,
                    "z_shieldcoinbase" => changed == method && generation == 0 ? -1 : -32601,
                    _ => changed == "privacy" && generation > 0 ? -32602 : -8,
                };
            }
            else if(method is "sendcurrency" or "z_shieldcoinbase" or "z_sendmany")
            {
                submissions.Add((generation, method, args?.Count ?? 0));
                // The fallback reaches a normal conclusive funds check. This fixture
                // never simulates a successful spend or persists a fictitious payment.
                code = generation > 0 && method == changed ? -32601 :
                    generation > 0 && changed == "privacy" && method == "z_sendmany" && args?.Count == 5 ? -32602 : -6;
            }
            await context.Response.WriteAsync(new JObject
            {
                ["id"] = request["id"], ["result"] = result,
                ["error"] = code.HasValue ? new JObject { ["code"] = code.Value, ["message"] = "conclusive test rejection" } : null,
            }.ToString());
        });
        var config = Config(server);
        await Cycle(config);
        Assert.Equal(3, probes);
        generation = 1;
        await Cycle(config);
        Assert.Equal(3, probes); // No probe or fallback spend in the rejected cycle.
        Assert.Single(submissions.Where(x => x.Generation == 1));
        generation = 2;
        await Cycle(config); // Same PoolConfig and endpoint; new handler instance.
        Assert.Equal(6, probes);
        var recovered = Assert.Single(submissions.Where(x => x.Generation == 2));
        Assert.Equal("z_sendmany", recovered.Method);
        Assert.Equal(changed == "privacy" ? 4 : 5, recovered.Arguments);
        await Cycle(config);
        Assert.Equal(6, probes); // The refreshed result is cached again.
    }

    [Fact]
    public async Task ShieldedBalanceReservesConventionalPageFeeBeforeSubmission()
    {
        foreach(var privacy in new[] { false, true })
        foreach(var count in new[] { 1, 50 })
        {
            var reserve = privacy ? .00005m * Math.Max(2, count + 1) : .0001m;
            var available = count + reserve - .00000001m;
            var submissions = 0;
            await using var server = await RpcDiagnosticTests.Server.Start(async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var request = JObject.Parse(await reader.ReadToEndAsync());
                var method = request.Value<string>("method");
                var args = request["params"] as JArray;
                JToken result = method switch
                {
                    "getblockchaininfo" => new JObject { ["chain"] = "main" },
                    "listunspent" => new JArray(),
                    "z_getbalance" => new JValue((double) available),
                    _ => JValue.CreateNull(),
                };
                int? code = method switch
                {
                    "sendcurrency" or "z_shieldcoinbase" => -32601,
                    "z_sendmany" => privacy ? -8 : -32602,
                    _ => null,
                };
                if(method == "z_sendmany" && args?.Last?.Value<string>() != "WrongPrivacyPolicy")
                {
                    submissions++;
                    Assert.Equal(count, ((JArray) args[1]).Count);
                    if(privacy) Assert.Equal(JTokenType.Null, args[3].Type);
                    else Assert.Equal(.0001m, args[3].Value<decimal>());
                    code = -6;
                }
                await context.Response.WriteAsync(new JObject
                {
                    ["id"] = request["id"], ["result"] = result,
                    ["error"] = code.HasValue ? new JObject { ["code"] = code.Value, ["message"] = "test rejection" } : null,
                }.ToString());
            });
            var config = Config(server);
            await Cycle(config, count);
            Assert.Equal(0, submissions);
            available += .00000001m;
            await Cycle(config, count);
            Assert.Equal(1, submissions);
        }
    }
}
