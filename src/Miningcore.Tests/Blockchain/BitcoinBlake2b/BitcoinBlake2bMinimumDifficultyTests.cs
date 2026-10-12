using System.Collections.Generic;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
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
