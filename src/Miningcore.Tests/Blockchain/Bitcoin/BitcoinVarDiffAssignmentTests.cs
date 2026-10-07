using System;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPublicationFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CanonicalVarDiff_PublicationFinishesBeforeFixedAuthorizeOrConfigure(bool idle, bool authorize)
    {
        var (config, manager, clock, bus) = Fixture();
        var time = new ManualTimeProvider();
        var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 1, VariancePercent = 1 };
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1, varDiff: options, varDiffTimeProvider: time);
        await wire.SendRequestAsync("mining.subscribe", "canonical-race");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        await wire.RetargetVarDiffAsync(false);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        var calculated = Barrier();
        var release = Barrier();
        var waiting = Barrier();
        wire.Canonical.BeforeVarDiffPublication = async () =>
        {
            calculated.TrySetResult();
            await release.Task.WaitAsync(Timeout);
        };
        wire.Canonical.AssignmentWaiting = () => waiting.TrySetResult();
        var update = wire.RetargetVarDiffAsync(idle);
        try
        {
            await calculated.Task.WaitAsync(Timeout);
            await wire.SendRequestAsync(authorize ? "mining.authorize" : "mining.configure",
                authorize ? new object[] { "miner.worker", "d=3" } : new object[] { new[] { "minimum-difficulty" },
                    new System.Collections.Generic.Dictionary<string, object> { ["minimum-difficulty.value"] = 3 } });
            if(authorize)
                Assert.True((await wire.ReadAsync())["result"].Value<bool>());
            await waiting.Task.WaitAsync(Timeout);
            Assert.NotNull(wire.Connection.Context.VarDiff);
            Assert.Equal(1, wire.Connection.Context.Difficulty);
        }
        finally { release.TrySetResult(); }
        await update.WaitAsync(Timeout);
        var dynamic = await wire.ReadAsync();
        Assert.Equal("mining.set_difficulty", dynamic["method"].Value<string>());
        Assert.Equal(2, dynamic["params"][0].Value<double>());
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        if(!authorize)
            Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        if(authorize)
        {
            var fixedMessage = await wire.ReadAsync();
            Assert.Equal("mining.set_difficulty", fixedMessage["method"].Value<string>());
            Assert.Equal(3, fixedMessage["params"][0].Value<double>());
        }
        await wire.SendRequestAsync("mining.extranonce.subscribe"); // FIFO fence against stale publication.
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Null(wire.Connection.Context.VarDiff);
        Assert.Equal(3, wire.Connection.Context.Difficulty);
        Assert.False(wire.Connection.Context.HasPendingDifficulty);
        await wire.RetargetVarDiffAsync(idle);
        Assert.False(wire.Connection.Context.ApplyPendingDifficulty());
    }
}
