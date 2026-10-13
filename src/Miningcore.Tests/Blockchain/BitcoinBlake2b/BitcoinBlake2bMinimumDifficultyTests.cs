using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Fact]
    public async Task FallingChainCeiling_BroadcastPublishesLowerImmutableWorkForOnTargetMiner()
    {
        var (config, manager, clock, bus) = Fixture("1e00ffff");
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            difficulty: 0.001, varDiffTimeProvider: time,
            varDiff: new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 10 });
        await Subscribe(wire);
        await wire.SendRequestAsync("mining.authorize", "test.worker", "x");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var old = Assert.Single(context.validJobs);
        await wire.RetargetVarDiffAsync(false);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        await wire.RetargetVarDiffAsync(false);
        await Fence(wire);
        var (_, lower, _, _) = Fixture("1f00ffff");
        var template = Assert.IsType<BitcoinBlake2bJob>(lower.GetJobForStratum());
        manager.SetCurrentJob(template);
        await wire.AnnounceJobAsync(template.GetJobParams(false));
        await Assignment(wire, manager.EasiestNetworkDifficulty.Value);
        await Fence(wire);
        Assert.NotNull(context.VarDiff);
        Assert.Contains(old, context.validJobs);
        Assert.False(context.HasPendingDifficulty);
        Assert.Equal(2, wire.JobsCreated);
    }

    [Fact]
    public async Task PortEqualMinimum_RetainsVarDiffWithoutRepublishingClampedNoOp()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            varDiff: new VarDiffConfig { MinDiff = 1e-9, TargetTime = 10, RetargetTime = 1 });
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var dynamic = context.VarDiff;
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 1e-9 });
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await wire.UpdateVarDiffAsync(0.5e-9);
        await Fence(wire);
        Assert.Equal(1, wire.JobsCreated);
        Assert.Same(dynamic, context.VarDiff);
        Assert.Equal(1e-9, context.NegotiatedMinimumDifficulty);
        await wire.UpdateVarDiffAsync(2e-9);
        await Assignment(wire, 2e-9);
        Assert.Same(dynamic, context.VarDiff);
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("reserved")]
    [InlineData("limit")]
    public async Task InvalidEnvelope_AfterBudgetExhaustionCannotEchoReservedResultKeys(string shape)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        for(var i = 0; i < Miningcore.Blockchain.BitcoinBlake2b.DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRequestAsync("mining.suggest_difficulty", 0);
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        }
        var extensions = shape == "limit"
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, 33), i => "unknown-" + i))
            : new[] { shape == "empty" ? "" : "version-rolling.mask", "minimum-difficulty" };
        await wire.SendRequestAsync("mining.configure", extensions,
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 8e-9 });
        var response = await wire.ReadAsync();
        Assert.NotNull(response["error"]);
        Assert.Equal(JTokenType.Boolean, response["result"].Type);
        Assert.False(response["result"].Value<bool>());
        await Fence(wire);
        Assert.Equal(1, wire.JobsCreated);
        Assert.Equal(1e-9, wire.Connection.Context.Difficulty);
        Assert.Null(wire.Connection.ContextAs<BitcoinWorkerContext>().NegotiatedMinimumDifficulty);
    }
    [Fact]
    public async Task MinimumFloor_NiceHashAndSuggestionCannotOverrideAcceptedDeviceConstraint()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        wire.NicehashLookup = _ => Task.FromResult<double?>(2e-9);
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 8e-9 });
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await wire.SendRequestAsync("mining.subscribe", "NiceHash/1.0");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        await Assignment(wire, 8e-9);
        await wire.SendRequestAsync("mining.suggest_difficulty", 2e-9);
        Assert.NotNull((await wire.ReadAsync())["error"]);
        await Fence(wire);
        Assert.Equal(8e-9, wire.Connection.ContextAs<BitcoinWorkerContext>().NegotiatedMinimumDifficulty);
        Assert.Equal(8e-9, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinimumConfigure_PreservesSinglePublicationAndImmutablePriorTarget(bool preSubscription)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        BitcoinBlake2bJob original = null;
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        if(!preSubscription)
        {
            await Subscribe(wire);
            original = Assert.IsType<BitcoinBlake2bJob>(Assert.Single(context.validJobs));
        }
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty", "unknown", "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = "0.000000002" });
        var result = (await wire.ReadAsync())["result"];
        Assert.True(result["minimum-difficulty"].Value<bool>());
        Assert.False(result["unknown"].Value<bool>());
        if(preSubscription)
        {
            await Fence(wire);
            Assert.Empty(context.validJobs);
            Assert.Equal(0, wire.JobsCreated);
            await Subscribe(wire);
        }
        else
            await Assignment(wire, 2e-9);
        await Fence(wire);
        var count = wire.JobsCreated;
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 2e-9 });
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Fence(wire);
        Assert.Equal(count, wire.JobsCreated);
        Assert.Equal(2e-9, context.Difficulty);
        Assert.Null(context.VarDiff);
        Assert.False(context.HasPendingDifficulty);
        if(original != null)
        {
            Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(BitcoinBlake2bHeader.TargetForDifficulty(1e-9)).ToString("x8"),
                JArray.FromObject(original.GetJobParams(false))[6].Value<string>());
            Assert.Same(original, context.GetJob(original.JobId));
        }
    }
}
