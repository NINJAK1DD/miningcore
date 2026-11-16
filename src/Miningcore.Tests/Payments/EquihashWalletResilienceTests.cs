using System;
using System.Collections.Generic;
using System.IO;
using System.Data;
using System.Linq;
using System.Reactive.Linq;
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
using Miningcore.Payments;
using Miningcore.Notifications.Messages;
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
    private async Task Cycle(PoolConfig config, int recipients = 1, TimeProvider time = null, IMessageBus bus = null)
    {
        var cf = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        cf.OpenConnectionAsync().Returns(connection);
        var paymentRepo = Substitute.For<IPaymentRepository>();
        paymentRepo.TryBeginPaymentBatchAsync(connection, transaction, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>()).Returns(true);
        var handler = new EquihashPayoutHandler(container, cf, container.Resolve<IMapper>(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
            paymentRepo, Substitute.For<IMasterClock>(), bus ?? Substitute.For<IMessageBus>(),
            new ActiveBlockGracePeriodTracker()) { CapabilityClock = time ?? TimeProvider.System };
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

    private sealed class CapabilityTime : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapabilityRefreshAdoptsNewSupportAndRepeatedRejectionAlerts(bool repeatedRejection)
    {
        var time = new CapabilityTime();
        var upgraded = false;
        var probes = 0;
        var nativeSubmissions = 0;
        var rejected = true;
        var funded = true;
        var success = false;
        var privacyProbes = 0;
        var notifications = new List<PaymentNotification>();
        var bus = new MessageBus();
        using var observed = bus.Listen<PaymentNotification>().Subscribe(notifications.Add);
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var method = request.Value<string>("method");
            var args = request["params"] as JArray;
            var probe = (method is "sendcurrency" or "z_shieldcoinbase") && (args == null || args.Count == 0) ||
                method == "z_sendmany" && args?.Last?.Value<string>() == "WrongPrivacyPolicy";
            JToken result = method switch { "getblockchaininfo" => new JObject { ["chain"] = "main" },
                "listunspent" => new JArray(), "z_getbalance" => new JValue(funded ? 100d : 0d), _ => JValue.CreateNull() };
            int? code = null;
            if(probe)
            {
                probes++;
                if(method == "z_sendmany") privacyProbes++;
                code = method switch { "z_shieldcoinbase" => upgraded ? -1 : -32601, "sendcurrency" => -32601, _ => -8 };
            }
            else if(method == "z_shieldcoinbase") { nativeSubmissions++; code = -6; }
            else if(method == "z_sendmany")
            {
                code = success ? null : repeatedRejection && rejected ? -32602 : -6;
                if(success) result = new JValue("operation");
            }
            else if(method == "z_getoperationresult")
                result = new JArray(new JObject { ["id"] = "operation", ["status"] = "success", ["result"] = new JObject { ["txid"] = "transaction" } });
            await context.Response.WriteAsync(new JObject { ["id"] = request["id"], ["result"] = result,
                ["error"] = code.HasValue ? new JObject { ["code"] = code.Value, ["message"] = "rejected" } : null }.ToString());
        });
        var config = Config(server);
        await Cycle(config, time: time, bus: bus);
        Assert.Equal(3, probes);
        if(repeatedRejection)
        {
            Assert.DoesNotContain(notifications, x => x.Error?.Contains("repeatedly") == true);
            funded = false;
            await Cycle(config, time: time, bus: bus); // Skipping must preserve the first rejection.
            funded = true;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(6, probes);
            Assert.Single(notifications.Where(x => x.Error?.Contains("repeatedly") == true));
            rejected = false;
            await Cycle(config, time: time, bus: bus); // An ordinary rejection must not reset the streak.
            rejected = true;
            await Cycle(config, time: time, bus: bus);
            Assert.Single(notifications.Where(x => x.Error?.Contains("repeatedly") == true));
            time.Ticks = EquihashPayoutHandler.CapabilityAlertInterval.Ticks - 1;
            await Cycle(config, time: time, bus: bus);
            Assert.Single(notifications.Where(x => x.Error?.Contains("repeatedly") == true));
            time.Ticks++;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(2, notifications.Count(x => x.Error?.Contains("repeatedly") == true));
            success = true;
            await Cycle(config, time: time, bus: bus);
            Assert.Contains(notifications, x => x.Outcome == PaymentNotificationOutcome.Success);
            success = false;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(2, notifications.Count(x => x.Error?.Contains("repeatedly") == true));
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(3, notifications.Count(x => x.Error?.Contains("repeatedly") == true));
        }
        else
        {
            upgraded = true;
            time.Ticks = EquihashPayoutHandler.CapabilityRefreshInterval.Ticks - 1;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(3, probes);
            Assert.Equal(0, nativeSubmissions);
            time.Ticks++;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(5, probes);
            Assert.Equal(1, privacyProbes);
            Assert.Equal(1, nativeSubmissions);
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(5, probes); // Cached again, not probed on every cycle.
            time.Ticks = EquihashPayoutHandler.PrivacyRefreshInterval.Ticks - 1;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(1, privacyProbes);
            time.Ticks++;
            await Cycle(config, time: time, bus: bus);
            Assert.Equal(2, privacyProbes);
        }
    }

    [Theory]
    [InlineData(true, "lost-submit")]
    [InlineData(true, "missing-operation")]
    [InlineData(true, "lost-result")]
    [InlineData(true, "unsupported-result")]
    [InlineData(true, "unknown-status")]
    [InlineData(true, "missing-transaction")]
    [InlineData(false, "lost-result")]
    [InlineData(false, "unsupported-result")]
    [InlineData(false, "unknown-status")]
    public async Task UncertainShieldingNeverContinuesToPayoutOrReprobes(bool native, string failure)
    {
        var probes = 0;
        var shielding = 0;
        var payouts = 0;
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
                "listunspent" => new JArray(new JObject { ["spendable"] = true, ["address"] = "t-address", ["amount"] = 10 }),
                "z_getbalance" => new JValue(100d),
                _ => JValue.CreateNull(),
            };
            int? code = null;
            if(probe)
            {
                probes++;
                code = method switch { "sendcurrency" => -32601, "z_shieldcoinbase" => native ? -1 : -32601, _ => -8 };
            }
            else if(method == "z_shieldcoinbase" || method == "z_sendmany" && args?[0]?.Value<string>() == "t-address")
            {
                shielding++;
                code = failure == "lost-submit" ? -500 : null;
                result = native ? new JObject { ["opid"] = failure == "missing-operation" ? null : "shield-operation" } : new JValue("shield-operation");
            }
            else if(method == "z_getoperationresult")
            {
                code = failure switch { "lost-result" => -500, "unsupported-result" => -32601, _ => null };
                result = new JArray(new JObject { ["id"] = "shield-operation",
                    ["status"] = failure == "unknown-status" ? "unrecognized" : "success",
                    ["result"] = new JObject() });
            }
            else if(method == "z_sendmany") { payouts++; code = -6; }
            await context.Response.WriteAsync(new JObject
            {
                ["id"] = request["id"], ["result"] = result,
                ["error"] = code.HasValue ? new JObject { ["code"] = code.Value, ["message"] = "injected shielding outcome" } : null,
            }.ToString());
        });
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => Cycle(Config(server)));
        Assert.Equal(3, probes);
        Assert.Equal(1, shielding);
        Assert.Equal(0, payouts);
    }

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
