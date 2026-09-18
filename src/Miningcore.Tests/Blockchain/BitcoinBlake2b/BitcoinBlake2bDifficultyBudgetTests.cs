using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.VarDiff;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests : TestBase
{
    private readonly ITestOutputHelper output;
    public BitcoinBlake2bDifficultyBudgetTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateSubscribe_WarnsOnceThenDisconnectsWithoutWorkOrExtranonceMutation(bool exhausted)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("subscribe-test"));
        if(exhausted)
        {
            for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            {
                await Send(wire, false, 0);
                Assert.True((await wire.ReadAsync())["result"].Value<bool>());
            }
            await Refused(wire, true);
        }
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var extraNonce = context.ExtraNonce1;
        var jobs = context.validJobs.ToArray();
        var difficulty = context.Difficulty;
        await wire.SendRequestAsync("mining.subscribe", "stray-duplicate");
        var warning = await wire.ReadAsync();
        Assert.Equal((int) StratumError.Other, warning["error"]["code"].Value<int>());
        Assert.False(warning["result"].Value<bool>());
        await Fence(wire);
        Assert.Equal(extraNonce, context.ExtraNonce1);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(1, wire.JobsCreated);
        Assert.DoesNotContain(target.Logs, x => x.Contains("DuplicateSubscription"));
        await wire.SendDisconnectingBatchAsync(string.Join("\n", Enumerable.Range(1, 20).Select(i =>
            $"{{\"id\":{100 + i},\"method\":\"mining.subscribe\",\"params\":[\"repeat\"]}}")));
        await wire.AssertNoMoreMessagesAsync();
        Assert.Equal(1, wire.JobsCreated);
        Assert.Equal(extraNonce, context.ExtraNonce1);
        Assert.Equal(difficulty, context.Difficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Single(target.Logs.Where(x => x.Contains("DuplicateSubscription")));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "duplicate-subscribe"), Arg.Any<string>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[[\"minimum-difficulty\"]]")]
    [InlineData("[[\"minimum-difficulty\"],{}]")]
    [InlineData("[[\"minimum-difficulty\"],null]")]
    [InlineData("[[\"minimum-difficulty\"],[]]")]
    [InlineData("[[null],{}]")]
    [InlineData("[[123],{}]")]
    [InlineData("[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":\"invalid\"}]")]
    [InlineData("[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":null}]")]
    [InlineData("[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":-1}]")]
    [InlineData("[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":1e999}]")]
    public async Task MalformedConfigure_ReturnsErrorWithoutMutationAndConsumesAdmission(string parameters)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var varDiff = context.VarDiff = new VarDiffContext { Config = new VarDiffConfig { MinDiff = 1e-9 } };
        context.EnqueueNewDifficulty(6e-9);
        await wire.SendRawAsync($"{{\"id\":100,\"method\":\"mining.configure\",\"params\":{parameters}}}");
        Assert.Equal((int) StratumError.Other, (await wire.ReadAsync())["error"]["code"].Value<int>());
        await Fence(wire);
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Same(varDiff, context.VarDiff);
        Assert.True(context.HasPendingDifficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(1, wire.JobsCreated);
        for(var i = 0; i < DifficultyRequestBudget.Capacity - 1; i++)
            await Accepted(wire, true, (i + 2) / 1e9);
        await Refused(wire, true);
    }

    [Theory]
    [InlineData("mining.suggest_difficulty", "[0.000000002]")]
    [InlineData("mining.configure", "[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":0.000000002}]")]
    [InlineData("mining.authorize", "[\"test.worker\",\"d=0.000000002\"]")]
    public async Task NullId_DoesNotChargeBudget(string method, string parameters)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        await wire.SendRawAsync($"{{\"id\":null,\"method\":\"{method}\",\"params\":{parameters}}}");
        Assert.Equal((int) StratumError.MinusOne, (await wire.ReadAsync())["error"]["code"].Value<int>());
        await Fence(wire);
        Assert.Equal(1e-9, wire.Connection.Context.Difficulty);
        Assert.Equal(0, manager.AddressValidations);
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, true, (i + 2) / 1e9);
        await Refused(wire, true);
    }

    [Fact]
    public void CustomTemplate_CannotEnableVersionRollingAtPoolConfigure()
    {
        var (config, manager, clock, bus) = Fixture();
        config.Template = new BitcoinBlake2bTemplate { DisableVersionRolling = false };
        Assert.Throws<PoolStartupException>(() =>
            new BitcoinBlake2bWireSession(container, clock, config, manager, bus));
    }

    [Theory]
    [InlineData("d={0}")]
    [InlineData("x;d={0};other=value")]
    [InlineData("prefixd={0}")]
    public async Task RepeatedStaticDifficultyAuthorization_CannotBypassBudget(string passwordFormat)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        var before = wire.JobsCreated;
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
        {
            var requested = (i + 2) / 1e9;
            await wire.SendRequestAsync("mining.authorize", "test.worker", Password(requested, passwordFormat));
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
            await Assignment(wire, requested);
        }
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var difficulty = context.Difficulty;
        var previousDifficulty = context.PreviousDifficulty;
        var jobs = context.validJobs.ToArray();
        var varDiff = context.VarDiff = new VarDiffContext { Config = new VarDiffConfig { MinDiff = 1e-9 } };
        context.EnqueueNewDifficulty(6e-9);
        var validations = manager.AddressValidations;
        for(var i = 0; i < DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
        {
            await wire.SendRequestAsync("mining.authorize", "replacement.worker", Password((i + 10) * 1e-9, passwordFormat));
            var response = await wire.ReadAsync();
            Assert.Equal((int) StratumError.Other, response["error"]?["code"]?.Value<int>());
            Assert.False(response["result"].Value<bool>());
            await Fence(wire);
            Assert.True(context.IsAuthorized);
            Assert.Equal("test", context.Miner);
            Assert.Equal(difficulty, context.Difficulty);
            Assert.Equal(previousDifficulty, context.PreviousDifficulty);
            Assert.Same(varDiff, context.VarDiff);
            Assert.True(context.HasPendingDifficulty);
            Assert.Equal(jobs, context.validJobs.ToArray());
            Assert.Equal(before + DifficultyRequestBudget.Capacity, wire.JobsCreated);
            Assert.Equal(validations, manager.AddressValidations);
        }
        await wire.SendRequestAsync("mining.authorize", "replacement.worker", Password(100e-9, passwordFormat));
        await wire.AssertDisconnectedAsync();
        Assert.Equal(before + DifficultyRequestBudget.Capacity, wire.JobsCreated);
    }

    private static string Password(double difficulty, string format = "d={0}") =>
        string.Format(CultureInfo.InvariantCulture, format, difficulty.ToString("0.000000000", CultureInfo.InvariantCulture));

    [Fact]
    public async Task CanonicalBitcoin_DifficultyAndAuthorizationRemainUnbudgeted()
    {
        var (config, manager, clock, bus) = Fixture();
        config.Template = ModuleInitializer.CoinTemplates["bitcoin"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        for(var i = 0; i < 20; i++)
        {
            var difficulty = (i + 2) / 1e9;
            if(i % 3 == 0)
                await wire.SendRequestAsync("mining.authorize", "test.worker", Password(difficulty));
            else
                await Send(wire, i % 3 == 1, difficulty);
            var response = await wire.ReadAsync();
            Assert.Null(response["method"]);
            Assert.True((i % 3 == 1 ? response["result"]["minimum-difficulty"] : response["result"]).Value<bool>());
            if(i % 3 != 1)
                Assert.Equal(difficulty, (await wire.ReadAsync())["params"][0].Value<double>());
            Assert.Equal(difficulty, wire.Connection.Context.Difficulty);
        }
        // Canonical Bitcoin still issues work only at subscribe here.
        Assert.Equal(1, wire.JobsCreated);
        Assert.Equal(7, manager.AddressValidations);
        // Documents existing canonical behavior, not a safe resubscription contract:
        // extranonce rotation can strand old jobs. Tracked in issue #181; keep this
        // scope regression until the canonical Bitcoin-family policy is fixed.
        await Subscribe(wire);
        Assert.Equal(2, wire.JobsCreated);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission), Arg.Any<string>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AlternatingRequests_BoundJobsAndReplies_ThenDisconnect(int mode)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        var before = wire.JobsCreated;
        var timer = Stopwatch.StartNew();
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("budget-test"));
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, mode == 1 || mode == 2 && i % 2 == 0, i % 2 == 0 ? 2e-9 : 3e-9);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var varDiff = context.VarDiff = new VarDiffContext { Config = new VarDiffConfig { MinDiff = 1e-9 } };
        context.EnqueueNewDifficulty(6e-9);
        for(var i = 0; i < 7; i++)
            await Refused(wire, mode == 1 || mode == 2 && i % 2 == 0);
        Assert.DoesNotContain(target.Logs, x => x.Contains("DifficultyBudgetDisconnect"));
        bus.Received(7).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "difficulty-refused"), Arg.Any<string>());
        Assert.Equal(3e-9, context.Difficulty);
        Assert.Same(varDiff, context.VarDiff);
        Assert.True(context.HasPendingDifficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(DifficultyRequestBudget.Capacity, wire.JobsCreated - before);
        output.WriteLine("Mode {0}: {1} admitted requests and jobs, seven refusal replies in {2:F2} ms; next request disconnects",
            mode, DifficultyRequestBudget.Capacity, timer.Elapsed.TotalMilliseconds);
        await wire.SendDisconnectingBatchAsync(string.Join("\n", Enumerable.Range(1, 20).Select(i =>
            $"{{\"id\":{100 + i},\"method\":\"mining.suggest_difficulty\",\"params\":[0.000000002]}}")));
        await wire.AssertNoMoreMessagesAsync();
        Assert.Single(target.Logs.Where(x => x.Contains("DifficultyBudgetDisconnect")));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "difficulty-disconnect"), Arg.Any<string>());
        Assert.Equal(DifficultyRequestBudget.Capacity, wire.JobsCreated - before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConfigureRefusal_MatchesSuccessShape_AndDuplicateExtensionNamesCannotMultiplyBudget(bool nicehash, bool asicBoost)
    {
        var (config, manager, clock, bus) = Fixture();
        config.EnableAsicBoost = asicBoost;
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        if(nicehash)
            wire.Connection.Context.UserAgent = "NiceHash/budget-test";
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty", "minimum-difficulty" },
                new Dictionary<string, object> { ["minimum-difficulty.value"] = (i + 2) * 1e-9 });
            var accepted = await wire.ReadAsync();
            Assert.True(accepted["result"]["minimum-difficulty"].Value<bool>());
            Assert.Equal(nicehash || asicBoost ? JTokenType.Null : (JTokenType?) null, accepted["error"]?.Type);
            await Assignment(wire, (i + 2) * 1e-9);
        }
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty", "version-rolling", "unknown" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 1e-8 });
        var response = await wire.ReadAsync();
        Assert.Equal(nicehash || asicBoost ? JTokenType.Null : (JTokenType?) null, response["error"]?.Type);
        Assert.Equal(JTokenType.String, response["result"]["minimum-difficulty"].Type);
        Assert.False(response["result"]["version-rolling"].Value<bool>());
        Assert.False(response["result"]["unknown"].Value<bool>());
        Assert.Equal((DifficultyRequestBudget.Capacity + 1) * 1e-9, wire.Connection.Context.Difficulty);
        await Fence(wire);
    }

    [Fact]
    public async Task AdmittedAuthorization_ResetsSevenRefusals_OrdinaryAuthorizationDoesNot()
    {
        var (config, manager, clock, bus) = Fixture();
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: time);
        await Subscribe(wire);
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, false, (i + 2) / 1e9);
        for(var i = 0; i < DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
            await Refused(wire, i % 2 == 0);
        time.AdvanceMonotonic(DifficultyRequestBudget.RefillInterval);
        await wire.SendRequestAsync("mining.authorize", "test.worker", Password(10e-9));
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await Assignment(wire, 10e-9);
        for(var i = 0; i < DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
            await Refused(wire, i % 2 == 0);
        await wire.SendRequestAsync("mining.authorize", "test.worker", "ordinary-password");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await Fence(wire);
        await Send(wire, false, 20e-9);
        await wire.AssertDisconnectedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicates_ConsumeBudgetWithoutIssuingJobs(bool configure)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        await Accepted(wire, configure, 2e-9);
        var before = wire.JobsCreated;
        for(var i = 0; i < DifficultyRequestBudget.Capacity - 1; i++)
        {
            await Send(wire, configure, 2e-9);
            Assert.Null((await wire.ReadAsync())["method"]);
            // The inherited suggest handler still writes set_difficulty for a duplicate.
            if(!configure)
                Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        }
        await Refused(wire, !configure);
        await Fence(wire);
        Assert.Equal(before, wire.JobsCreated);
    }

    [Fact]
    public async Task MonotonicRecovery_IgnoresWallClock_AndPreservesFractionalRefillAndCapacity()
    {
        var (config, manager, clock, bus) = Fixture();
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: time);
        await Subscribe(wire);
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, false, (i + 2) * 1e-9);
        foreach(var delta in new[] { TimeSpan.FromDays(365), TimeSpan.FromDays(-730) })
        {
            time.MoveWallClock(delta);
            clock.Now.Returns(time.GetUtcNow().UtcDateTime);
            await Refused(wire, true);
        }
        time.AdvanceMonotonic(TimeSpan.FromSeconds(9));
        await Refused(wire, false);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await Accepted(wire, true, 6e-9);
        await Refused(wire, false);
        time.AdvanceMonotonic(TimeSpan.FromDays(30));
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, i % 2 == 0, (i + 7) * 1e-9);
        await Refused(wire, true);
        await Fence(wire);
    }

    [Fact]
    public async Task PipelinedBurst_SharedMethods_StopBeforeMutation_AndLeaveOldProofBindingIntact()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        // Spend the extra startup headroom before testing a bounded pipelined batch.
        for(var i = 0; i < DifficultyRequestBudget.Capacity - 4; i++)
        {
            await Send(wire, false, 0);
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        }
        // Five requests fit below the dispatcher's 32 KiB and 16-response bounds.
        for(var i = 0; i < 5; i++)
            await Send(wire, i % 2 == 0, (i + 2) * 1e-9);
        for(var i = 0; i < 4; i++)
        {
            Assert.Null((await wire.ReadAsync())["method"]);
            await Assignment(wire, (i + 2) * 1e-9);
        }
        Assert.Equal(JTokenType.String, (await wire.ReadAsync())["result"]["minimum-difficulty"].Type);
        await Fence(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        Assert.Equal(5e-9, context.Difficulty);
        var oldJob = Assert.IsType<BitcoinBlake2bJob>(context.validJobs.First());
        var parameters = (object[]) oldJob.GetJobParams(false);
        var nonce = new byte[8];
        for(ulong i = 0; i < 100000; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(nonce, i);
            try
            {
                var share = oldJob.ProcessShare(wire.Connection, "0000000000000000",
                    (string) parameters[7], nonce.ToHexString()).Share;
                Assert.Equal(2e-9, share.Difficulty);
                return;
            }
            catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
        }
        Assert.Fail("No easy regtest proof found");
    }

    [Fact]
    public async Task PreSubscribeBudget_DoesNotBlockSubscribeAuthorizeOrServerVarDiff_AndConnectionsAreIndependent()
    {
        var (config, manager, clock, bus) = Fixture();
        var time = new ManualTimeProvider();
        await using var first = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, budgetTimeProvider: time);
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
        {
            // Below-base requests do no useful work but must still consume admission.
            await Send(first, false, 0);
            Assert.True((await first.ReadAsync())["result"].Value<bool>());
        }
        await Refused(first, true);
        await Subscribe(first);
        var jobs = first.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray();
        for(var i = 0; i < 3; i++)
        {
            await first.SendRequestAsync("mining.authorize", "test.worker", Password((i + 2) / 1e9));
            var refusal = await first.ReadAsync();
            Assert.Equal((int) StratumError.Other, refusal["error"]["code"].Value<int>());
            Assert.False(refusal["result"].Value<bool>());
            Assert.False(first.Connection.Context.IsAuthorized);
            Assert.Equal(1e-9, first.Connection.Context.Difficulty);
            Assert.Equal(jobs, first.Connection.ContextAs<BitcoinWorkerContext>().validJobs.ToArray());
            Assert.Equal(1, first.JobsCreated);
            Assert.Equal(0, manager.AddressValidations);
            await Fence(first);
        }
        // Authentication without a static-difficulty request remains responsive.
        await first.SendRequestAsync("mining.authorize", "test.worker", "ordinary-password");
        Assert.True((await first.ReadAsync())["result"].Value<bool>());
        Assert.True(first.Connection.Context.IsAuthorized);
        Assert.Equal(1, manager.AddressValidations);
        await Fence(first);
        await first.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false));
        Assert.Equal("mining.notify", (await first.ReadAsync())["method"].Value<string>());
        await first.UpdateVarDiffAsync(3e-9);
        await Assignment(first, 3e-9);
        await Refused(first, false);
        await Fence(first);

        await using var second = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, first);
        await Subscribe(second);
        await Accepted(second, true, 4e-9);
        Assert.Equal(3e-9, first.Connection.Context.Difficulty);
        Assert.Equal(4e-9, second.Connection.Context.Difficulty);
    }

    private static async Task Refused(BitcoinBlake2bWireSession wire, bool configure)
    {
        await Send(wire, configure, 100e-9);
        var response = await wire.ReadAsync();
        Assert.Null(response["method"]);
        Assert.NotNull(response["id"]);
        if(configure)
            Assert.Equal(RetryMessage(), response["result"]["minimum-difficulty"].Value<string>());
        else
        {
            Assert.Equal((int) StratumError.Other, response["error"]["code"].Value<int>());
            Assert.False(response["result"].Value<bool>());
            Assert.Equal(RetryMessage(), response["error"]["message"].Value<string>());
        }
    }

    private static string RetryMessage() => FormattableString.Invariant(
        $"Difficulty request rate limit exceeded; retry after {DifficultyRequestBudget.RefillInterval.TotalSeconds} seconds");

    private static async Task Fence(BitcoinBlake2bWireSession wire)
    {
        // Strict FIFO fence: no hidden set_difficulty/notify may precede this response.
        await wire.SendRequestAsync("mining.configure", new[] { "version-rolling" }, new Dictionary<string, object>());
        var response = await wire.ReadAsync();
        Assert.Null(response["method"]);
        Assert.False(response["result"]["version-rolling"].Value<bool>());
    }

    private static async Task Subscribe(BitcoinBlake2bWireSession wire)
    {
        Assert.NotNull((await wire.RequestAsync("mining.subscribe", "budget-test"))["result"]);
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
    }

    private static Task Send(BitcoinBlake2bWireSession wire, bool configure, double difficulty) =>
        configure ? wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = difficulty }) :
            wire.SendRequestAsync("mining.suggest_difficulty", difficulty);

    private static async Task Accepted(BitcoinBlake2bWireSession wire, bool configure, double difficulty)
    {
        await Send(wire, configure, difficulty);
        var response = await wire.ReadAsync();
        Assert.Null(response["method"]);
        Assert.True((configure ? response["result"]["minimum-difficulty"] : response["result"]).Value<bool>());
        await Assignment(wire, difficulty);
    }

    private static async Task Assignment(BitcoinBlake2bWireSession wire, double difficulty)
    {
        var set = await wire.ReadAsync();
        Assert.Equal("mining.set_difficulty", set["method"].Value<string>());
        Assert.Equal(difficulty, set["params"][0].Value<double>());
        var notify = await wire.ReadAsync();
        Assert.Equal("mining.notify", notify["method"].Value<string>());
        Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(
            BitcoinBlake2bHeader.TargetForDifficulty(difficulty)).ToString("x8"), notify["params"][6].Value<string>());
        var job = wire.Connection.ContextAs<BitcoinWorkerContext>().GetJob(notify["params"][0].Value<string>());
        Assert.NotNull(job);
    }

    private (PoolConfig, FixtureManager, IMasterClock, IMessageBus) Fixture()
    {
        var coin = (BitcoinBlake2bTemplate) ModuleInitializer.CoinTemplates["bitcoin-blake2b"];
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var config = new PoolConfig
        {
            Id = "difficulty-budget", Coin = "bitcoin-blake2b", Template = coin,
            Banning = new PoolShareBasedBanningConfig { Enabled = false },
        };
        var job = new BitcoinBlake2bJob();
        job.InitBlake2b(new BlockTemplate
        {
            Height = 21, Version = 0xa0000000, CurTime = 1700000000, Bits = "207fffff",
            Target = BitcoinBlake2bHeader.DecodeCompactTarget(0x207fffff).ToString("x").PadLeft(64, '0'),
            PreviousBlockhash = new string('0', 64), CoinbaseValue = 5000000000,
            Transactions = Array.Empty<BitcoinBlockTransaction>(), Rules = new[] { "!blake2b" },
        }, "budget", config, new BitcoinPoolConfigExtra(), new ClusterConfig(), clock,
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest), Network.RegTest,
            coin.ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);
        return (config, new FixtureManager(container, clock, bus, job), clock, bus);
    }

    private sealed class FixtureManager : BitcoinBlake2bJobManager
    {
        internal int AddressValidations { get; private set; }
        internal Func<Task> BeforeValidation { get; set; }
        internal void SetCurrentJob(BitcoinBlake2bJob job) => currentJob = job;
        internal FixtureManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus, BitcoinBlake2bJob job) :
            base(ctx, clock, bus, new BitcoinBlake2bExtraNonceProvider())
        {
            network = Network.RegTest;
            currentJob = job;
        }
        public override async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
        {
            AddressValidations++;
            if(BeforeValidation != null)
                await BeforeValidation();
            return true;
        }
    }
}
