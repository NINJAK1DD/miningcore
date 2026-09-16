using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    [Theory]
    [InlineData("mining.configure")]
    [InlineData("mining.suggest_difficulty")]
    [InlineData("mining.authorize")]
    public async Task UnrepresentableMinerDifficulty_IsChargedAndRejectedBeforeAnyMutation(string method)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        Assert.True((await wire.RequestAsync("mining.authorize", "original.worker", "x"))["result"].Value<bool>());
        await Fence(wire); // Authorization finishes session setup after its ack.
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var varDiff = context.VarDiff = new VarDiffContext { Config = new VarDiffConfig { MinDiff = 1e-9 } };
        context.EnqueueNewDifficulty(3e-9);
        var jobs = context.validJobs.ToArray();
        var validations = manager.AddressValidations;
        var sessionId = context.SessionId;
        var responses = wire.Connection.ResponseSequence;
        object[] parameters = method switch
        {
            "mining.configure" => new object[] { new[] { "minimum-difficulty" },
                new Dictionary<string, object> { ["minimum-difficulty.value"] = 1e100 } },
            "mining.suggest_difficulty" => new object[] { 1e100 },
            // The legacy static parser consumes decimal notation, not exponents.
            _ => new object[] { "replacement.worker", "d=1" + new string('0', 100) },
        };
        await wire.SendRawAsync(JsonConvert.SerializeObject(new { id = 100, method, @params = parameters }) +
            "\n{\"id\":101,\"method\":\"mining.extranonce.subscribe\",\"params\":[]}");
        var rejected = await wire.ReadAsync();
        Assert.Equal(100, rejected["id"].Value<int>());
        Assert.Equal((int) StratumError.Other, rejected["error"]["code"].Value<int>());
        Assert.False(rejected["result"].Value<bool>());
        var fence = await wire.ReadAsync();
        Assert.Equal(101, fence["id"].Value<int>());
        Assert.Null(fence["error"]);
        Assert.Equal(2, wire.Connection.ResponseSequence - responses);
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Same(varDiff, context.VarDiff);
        Assert.Equal(jobs, context.validJobs.ToArray());
        Assert.Equal(1, wire.JobsCreated);
        Assert.Equal("original", context.Miner);
        Assert.Equal("worker", context.Worker);
        Assert.True(context.IsAuthorized);
        Assert.Equal(sessionId, context.SessionId);
        Assert.Equal(validations, manager.AddressValidations);
        Assert.True(context.ApplyPendingDifficulty());
        Assert.Equal(3e-9, context.Difficulty);
        // Exactly one token was spent; healthy assignment publication still works.
        for(var i = 1; i < DifficultyRequestBudget.Capacity; i++)
            await Accepted(wire, true, (i + 4) / 1e9);
        await Refused(wire, true);
        await Fence(wire);
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedUnrepresentablePublication_LatchesAdmissionAndInvalidatesJobs(bool nicehash)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("representability-test"));
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        if(nicehash)
            wire.NicehashLookup = _ => Task.FromResult<double?>(1e100);
        else
        {
            await Subscribe(wire);
            // Unexpected future/external mutation after an otherwise valid ack
            // exercises CompleteAssignmentAsync's defensive validation branch.
            wire.AfterConfigure = () => { context.SetDifficulty(1e100); return Task.CompletedTask; };
        }
        var responses = wire.Connection.ResponseSequence;
        var jobs = wire.JobsCreated;
        var method = nicehash ? "mining.subscribe" : "mining.configure";
        await wire.SendDisconnectingBatchAsync(JsonConvert.SerializeObject(new { id = 100, method,
            @params = nicehash ? new object[] { "NiceHash/1.0" } : PublicationParameters(method) }) +
            "\n{\"id\":101,\"method\":\"mining.extranonce.subscribe\",\"params\":[]}");
        var messages = await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(1, wire.Connection.ResponseSequence - responses);
        Assert.DoesNotContain(messages, x => x["id"]?.Value<int?>() == 101);
        Assert.DoesNotContain(messages, x => x["error"]?.Type is not (null or JTokenType.Null));
        Assert.DoesNotContain(messages, x => x["method"]?.Value<string>() == "mining.notify");
        Assert.Equal(jobs, wire.JobsCreated);
        Assert.Empty(context.validJobs);
        if(!nicehash)
            Assert.Equal(1e-9, context.Difficulty);
        Assert.Single(target.Logs.Where(x => x.Contains("AssignmentPublicationFailure")));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public async Task SuggestPrevalidation_UsesTheSameCultureParsedValueForExecution(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var (config, manager, clock, bus) = Fixture();
            await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
            await Subscribe(wire);
            await wire.SendRequestAsync("mining.suggest_difficulty", "0,000000002");
            Assert.True((await wire.ReadAsync())["result"].Value<bool>());
            await Assignment(wire, 2e-9);
            await Fence(wire);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
