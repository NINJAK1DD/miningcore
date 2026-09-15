using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
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

public class BitcoinBlake2bDifficultyBudgetTests : TestBase
{
    private readonly ITestOutputHelper output;
    public BitcoinBlake2bDifficultyBudgetTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AlternatingRequests_BoundJobsAndReplies_ThenDisconnect(int mode)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        wire.BudgetTimeProvider = new ManualTimeProvider();
        await Subscribe(wire);
        var before = wire.JobsCreated;
        var timer = Stopwatch.StartNew();
        for(var i = 0; i < 4; i++)
            await Accepted(wire, mode == 1 || mode == 2 && i % 2 == 0, i % 2 == 0 ? 2e-9 : 3e-9);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        var varDiff = context.VarDiff = new VarDiffContext { Config = new VarDiffConfig { MinDiff = 1e-9 } };
        context.EnqueueNewDifficulty(6e-9);
        for(var i = 0; i < 7; i++)
            await Refused(wire, mode == 1 || mode == 2 && i % 2 == 0);
        Assert.Equal(3e-9, context.Difficulty);
        Assert.Same(varDiff, context.VarDiff);
        Assert.True(context.HasPendingDifficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(4, wire.JobsCreated - before);
        output.WriteLine("Mode {0}: 11 requests, 4 jobs, 11 responses, 4 set_difficulty, 4 notify in {1:F2} ms; next request disconnects",
            mode, timer.Elapsed.TotalMilliseconds);
        await Send(wire, mode == 1, 2e-9);
        await wire.AssertDisconnectedAsync();
        Assert.Equal(4, wire.JobsCreated - before);
    }

    [Fact]
    public async Task ConfigureRefusal_ReturnsEachExtension_AndDuplicateExtensionNamesCannotMultiplyBudget()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        wire.BudgetTimeProvider = new ManualTimeProvider();
        await Subscribe(wire);
        for(var i = 0; i < 4; i++)
        {
            await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty", "minimum-difficulty" },
                new Dictionary<string, object> { ["minimum-difficulty.value"] = (i + 2) * 1e-9 });
            Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
            await Assignment(wire, (i + 2) * 1e-9);
        }
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty", "version-rolling", "unknown" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 1e-8 });
        var response = await wire.ReadAsync();
        Assert.Equal(JTokenType.Null, response["error"].Type);
        Assert.Equal(JTokenType.String, response["result"]["minimum-difficulty"].Type);
        Assert.False(response["result"]["version-rolling"].Value<bool>());
        Assert.False(response["result"]["unknown"].Value<bool>());
        Assert.Equal(5e-9, wire.Connection.Context.Difficulty);
        await Fence(wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicates_ConsumeBudgetWithoutIssuingJobs(bool configure)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        wire.BudgetTimeProvider = new ManualTimeProvider();
        await Subscribe(wire);
        await Accepted(wire, configure, 2e-9);
        var before = wire.JobsCreated;
        for(var i = 0; i < 3; i++)
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
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        wire.BudgetTimeProvider = time;
        await Subscribe(wire);
        for(var i = 0; i < 4; i++)
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
        for(var i = 0; i < 4; i++)
            await Accepted(wire, i % 2 == 0, (i + 7) * 1e-9);
        await Refused(wire, true);
        await Fence(wire);
    }

    [Fact]
    public async Task PipelinedBurst_SharedMethods_StopBeforeMutation_AndLeaveOldProofBindingIntact()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        wire.BudgetTimeProvider = new ManualTimeProvider();
        await Subscribe(wire);
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
        Assert.True(false, "No easy regtest proof found");
    }

    [Fact]
    public async Task PreSubscribeBudget_DoesNotBlockSubscribeAuthorizeOrServerVarDiff_AndConnectionsAreIndependent()
    {
        var (config, manager, clock, bus) = Fixture();
        var time = new ManualTimeProvider();
        await using var first = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        first.BudgetTimeProvider = time;
        for(var i = 0; i < 4; i++)
        {
            // Below-base requests do no useful work but must still consume admission.
            await Send(first, false, 0);
            Assert.True((await first.ReadAsync())["result"].Value<bool>());
        }
        await Refused(first, true);
        await Subscribe(first);
        Assert.True((await first.RequestAsync("mining.authorize", "test.worker", "d=0.000000002"))["result"].Value<bool>());
        await Assignment(first, 2e-9);
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
            Assert.Contains("rate limit", response["result"]["minimum-difficulty"].Value<string>());
        else
        {
            Assert.Equal((int) StratumError.Other, response["error"]["code"].Value<int>());
            Assert.False(response["result"].Value<bool>());
        }
    }

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
        internal FixtureManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus, BitcoinBlake2bJob job) :
            base(ctx, clock, bus, new BitcoinBlake2bExtraNonceProvider())
        {
            network = Network.RegTest;
            currentJob = job;
        }
        public override Task<bool> ValidateAddressAsync(string address, CancellationToken ct) => Task.FromResult(true);
    }
}
