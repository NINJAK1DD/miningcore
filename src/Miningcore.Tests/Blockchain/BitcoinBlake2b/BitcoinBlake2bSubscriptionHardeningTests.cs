using System;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("123")]
    [InlineData("\"miner\"")]
    [InlineData("[{}]")]
    [InlineData("[[]]")]
    [InlineData("[\"NiceHash/1.0\",{}]")]
    public async Task MalformedSubscribe_IsBoundedBeforeLookupOrStateMutation(string parameters)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        var lookups = 0;
        wire.NicehashLookup = _ => { lookups++; return Task.FromResult<double?>(null); };
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var extraNonce = context.ExtraNonce1;
        for(var i = 0; i < DifficultyRequestBudget.Capacity + DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
        {
            await wire.SendRawAsync($"{{\"id\":100,\"method\":\"mining.subscribe\",\"params\":{parameters}}}");
            var response = await wire.ReadAsync();
            Assert.Equal(100, response["id"].Value<int>());
            Assert.Equal((int) StratumError.Other, response["error"]["code"].Value<int>());
            Assert.Equal(i < DifficultyRequestBudget.Capacity ? "Invalid request parameters" : RetryMessage(),
                response["error"]["message"].Value<string>());
            Assert.False(response["result"].Value<bool>());
        }
        Assert.Equal(0, lookups);
        Assert.Equal(0, wire.JobsCreated);
        Assert.False(context.IsSubscribed);
        Assert.Null(context.UserAgent);
        Assert.Equal(extraNonce, context.ExtraNonce1);
        Assert.Equal(1e-9, context.Difficulty);
        await Fence(wire);
        await wire.SendRawAsync($"{{\"id\":101,\"method\":\"mining.subscribe\",\"params\":{parameters}}}");
        await wire.AssertNoMoreMessagesAsync();
    }

    [Theory]
    [InlineData("null", null, false)]
    [InlineData("[]", null, false)]
    [InlineData("[null]", null, false)]
    [InlineData("[123]", "123", false)]
    [InlineData("[true]", "True", false)]
    [InlineData("[\"2026-09-16T00:00:00Z\"]", "09/16/2026 00:00:00", false)]
    [InlineData("[\"  NiceHash/1.0  \",\"session\",null,42,false]", "NiceHash/1.0", true)]
    public async Task Subscribe_UsesIdenticalPreparedIdentityAndRemainsFreeAfterMalformedExhaustion(
        string parameters, string userAgent, bool isNicehash)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        var lookups = 0;
        wire.NicehashLookup = context =>
        {
            lookups++;
            Assert.Equal(userAgent, context.UserAgent);
            Assert.Equal(isNicehash, context.IsNicehash);
            return Task.FromResult<double?>(null);
        };
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRawAsync("{\"id\":100,\"method\":\"mining.subscribe\",\"params\":{}}");
            Assert.Equal("Invalid request parameters", (await wire.ReadAsync())["error"]["message"].Value<string>());
        }
        await Refused(wire, true);
        Assert.Equal(0, lookups);
        await wire.SendRawAsync($"{{\"id\":101,\"method\":\"mining.subscribe\",\"params\":{parameters}}}");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        await Assignment(wire, 1e-9);
        Assert.Equal(1, lookups);
        Assert.Equal(1, wire.JobsCreated);
        Assert.True(wire.Connection.Context.IsSubscribed);
        Assert.Equal(userAgent, wire.Connection.Context.UserAgent);
        Assert.Equal(isNicehash, wire.Connection.Context.IsNicehash);
        await Refused(wire, true); // Successful subscription did not refill the bucket.
        await Fence(wire);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task SubscribeMissingId_DoesNotChargeOrPrepare(string parameters)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        var lookups = 0;
        wire.NicehashLookup = _ => { lookups++; return Task.FromResult<double?>(null); };
        await wire.SendRawAsync($"{{\"id\":null,\"method\":\"mining.subscribe\",\"params\":{parameters}}}");
        Assert.Equal((int) StratumError.MinusOne, (await wire.ReadAsync())["error"]["code"].Value<int>());
        Assert.Equal(0, lookups);
        Assert.Equal(0, wire.JobsCreated);
        Assert.False(wire.Connection.Context.IsSubscribed);
        await Subscribe(wire);
        for(var i = 0; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, true, (i + 2) / 1e9);
        await Refused(wire, true);
    }

    [Fact]
    public async Task PoolFaultDuringWorkerJobCreation_DisconnectsImmediately()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        wire.BeforeCreateJob = wire.FailJobPipeline;
        await Send(wire, true, 2e-9);
        // Prefix responses may already be queued; terminal closure must not wait
        // for another request when job creation observes the pool fault.
        await wire.AssertDisconnectedAsync();
        Assert.True(wire.MiningFaulted);
        Assert.Equal(1, wire.JobsCreated);
    }
}
