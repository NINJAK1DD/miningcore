using System;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using NLog.Targets;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Fact]
    public async Task CanonicalPlainAuthorizationDoesNotSpendOrDependOnDifficultyTokens()
    {
        var (config, manager, clock, bus) = Fixture();
        config.Template = ModuleInitializer.CoinTemplates["bitcoin"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, varDiffTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        for(var i = 0; i < 20; i++)
        {
            await wire.SendRequestAsync("mining.authorize", $"test.worker{i}", "x");
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        }
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRequestAsync("mining.suggest_difficulty", 1e-9);
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        }
        await wire.SendRequestAsync("mining.authorize", "another.worker", "x");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Equal(21, manager.AddressValidations);
        await wire.SendRequestAsync("mining.authorize", "another.worker", "d=10");
        Assert.False((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Equal(21, manager.AddressValidations);
        Assert.False(wire.Connection.IsDisconnectRequested);
    }

    [Fact]
    public async Task CanonicalNicehashAuthorizationStillSpendsDifficultyTokens()
    {
        var (config, manager, clock, bus) = Fixture();
        config.Template = ModuleInitializer.CoinTemplates["bitcoin"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, varDiffTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        wire.Connection.Context.UserAgent = "NiceHash/1.0";
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRequestAsync("mining.authorize", "test.worker", "x");
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        }
        await wire.SendRequestAsync("mining.authorize", "test.worker", "x");
        Assert.False((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Equal(DifficultyRequestBudget.Capacity, manager.AddressValidations);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("invalid")]
    public async Task CanonicalInvalidSuggestionIsRecoverableWithoutMutationOrPublication(string value)
    {
        var (config, manager, clock, bus) = Fixture();
        config.Template = ModuleInitializer.CoinTemplates["bitcoin"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, varDiffTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        using var logs = new LogFactory();
        var target = new MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Error, LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("invalid-suggestion"));
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var jobs = context.validJobs.ToArray();
        await wire.SendRequestAsync("mining.suggest_difficulty", value);
        var response = await wire.ReadAsync();
        Assert.False(response["result"].Value<bool>());
        Assert.Equal((int) StratumError.Other, response["error"]["code"].Value<int>());
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.False(wire.Connection.IsDisconnectRequested);
        Assert.Empty(target.Logs);
        await wire.SendRequestAsync("mining.suggest_difficulty", 2e-9);
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        var difficulty = await wire.ReadAsync();
        Assert.Equal("mining.set_difficulty", difficulty["method"].Value<string>());
        Assert.Equal(2e-9, difficulty["params"][0].Value<double>());
        var notify = await wire.ReadAsync();
        Assert.Equal("mining.notify", notify["method"].Value<string>());
        Assert.NotNull(context.GetJob(notify["params"][0].Value<string>()));
        var count = wire.JobsCreated;
        await wire.SendRequestAsync("mining.suggest_difficulty", 2e-9);
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await wire.SendRequestAsync("mining.extranonce.subscribe");
        var fence = await wire.ReadAsync();
        Assert.Null(fence["method"]);
        Assert.True(fence["result"].Value<bool>());
        Assert.Equal(count, wire.JobsCreated);
        Assert.Empty(target.Logs);
    }
}
