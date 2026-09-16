using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Extensions;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public partial class BitcoinBlake2bDifficultyBudgetTests
{
    private static object[] PublicationParameters(string method) => method switch
    {
        "mining.subscribe" => new object[] { "publication-test" },
        "mining.configure" => new object[] { new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 2e-9 } },
        "mining.suggest_difficulty" => new object[] { 2e-9 },
        "mining.authorize" => new object[] { "test.worker", Password(2e-9) },
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    [Theory]
    [InlineData("mining.configure")]
    [InlineData("mining.suggest_difficulty")]
    [InlineData("mining.authorize")]
    [InlineData("mining.subscribe")]
    public async Task UnavailableWorkerJob_AfterAcknowledgementClosesWithoutSecondResponse(string method)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        if(method != "mining.subscribe")
            await Subscribe(wire);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("publication-test"));
        var jobs = wire.JobsCreated;
        manager.SetCurrentJob(null);
        var responses = wire.Connection.ResponseSequence;
        var failed = JsonConvert.SerializeObject(new { id = 100, method, @params = PublicationParameters(method) });
        var buffered = JsonConvert.SerializeObject(new { id = 101, method = "mining.extranonce.subscribe", @params = new object[0] });
        await wire.SendDisconnectingBatchAsync(failed + "\n" + buffered);
        var messages = await wire.ReadUntilDisconnectedAsync();
        // Abortive close can discard queued output. The sequence additionally
        // proves a second response was never attempted, even if TCP loses the first.
        Assert.Equal(1, wire.Connection.ResponseSequence - responses);
        Assert.InRange(messages.Count(x => x["id"]?.Value<int?>() == 100), 0, 1);
        Assert.DoesNotContain(messages, x => x["id"]?.Value<int?>() == 101);
        Assert.DoesNotContain(messages, x => x["error"]?.Type is not (null or JTokenType.Null));
        Assert.DoesNotContain(messages, x => x["method"]?.Value<string>() == "mining.notify");
        Assert.Equal(jobs, wire.JobsCreated);
        Assert.False(wire.MiningFaulted); // A per-connection publication failure is not a pool fault.
        Assert.Single(target.Logs.Where(x => x.Contains("AssignmentPublicationFailure")));
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData("mining.configure")]
    [InlineData("mining.suggest_difficulty")]
    [InlineData("mining.authorize")]
    [InlineData("mining.subscribe")]
    public async Task SuccessfulPublication_HasOneResponseAndMatchingAssignment(string method)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        if(method != "mining.subscribe")
            await Subscribe(wire);
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRawAsync(JsonConvert.SerializeObject(new { id = 100, method, @params = PublicationParameters(method) }));
        var messages = new[] { await wire.ReadAsync(), await wire.ReadAsync(), await wire.ReadAsync() };
        Assert.Equal(100, messages[0]["id"].Value<int>());
        Assert.Null(messages[0]["error"]);
        var difficulty = method == "mining.subscribe" ? 1e-9 : 2e-9;
        Assert.Equal("mining.set_difficulty", messages[1]["method"].Value<string>());
        Assert.Equal(difficulty, messages[1]["params"][0].Value<double>());
        AssertNotifyDifficulty(messages[2], difficulty);
        Assert.Single(messages.Where(x => x["id"]?.Value<int?>() == 100));
        Assert.Equal(1, wire.Connection.ResponseSequence - responses);
        await Fence(wire); // No extra same-ID response or notification remains queued.
    }

    [Fact]
    public async Task ConfigureErrorAfterAcknowledgement_IsTerminalWithoutAnotherResponse()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        wire.AfterConfigure = () => throw new StratumException(StratumError.Other, "completion failed");
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRequestAsync("mining.configure", PublicationParameters("mining.configure"));
        var messages = await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(1, wire.Connection.ResponseSequence - responses);
        Assert.DoesNotContain(messages, x => x["error"]?.Type is not (null or JTokenType.Null));
        Assert.Equal(1, wire.JobsCreated);
    }

    [Fact]
    public async Task SubscribeErrorBeforeAcknowledgement_PreservesStateAndAllowsRetry()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var extraNonce = context.ExtraNonce1;
        wire.NicehashLookup = _ => throw new StratumException(StratumError.JobNotFound, "lookup unavailable");
        var responses = wire.Connection.ResponseSequence;
        var error = await wire.RequestAsync("mining.subscribe", "NiceHash/1.0");
        Assert.Equal((int) StratumError.JobNotFound, error["error"]["code"].Value<int>());
        Assert.Equal(1, wire.Connection.ResponseSequence - responses);
        Assert.False(context.IsSubscribed);
        Assert.Null(context.UserAgent);
        Assert.Equal(extraNonce, context.ExtraNonce1);
        Assert.Equal(1e-9, context.Difficulty);
        Assert.Equal(0, wire.JobsCreated);
        wire.NicehashLookup = null;
        await Subscribe(wire);
        await Fence(wire);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SubscribeOptionalParameters_AcceptsOmittedAndNullForBothPools(bool canonical, bool explicitNull)
    {
        var (config, manager, clock, bus) = Fixture();
        if(canonical)
            config.Template = ModuleInitializer.CoinTemplates["bitcoin"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: canonical);
        await wire.SendRawAsync(explicitNull ? "{\"id\":100,\"method\":\"mining.subscribe\",\"params\":null}" :
            "{\"id\":100,\"method\":\"mining.subscribe\"}");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        var difficulty = await wire.ReadAsync();
        Assert.Equal("mining.set_difficulty", difficulty["method"].Value<string>());
        Assert.Equal(1e-9, difficulty["params"][0].Value<double>());
        var notify = await wire.ReadAsync();
        Assert.Equal("mining.notify", notify["method"].Value<string>());
        if(canonical)
            // Canonical Bitcoin publishes chain nBits, not the BLAKE2b share target.
            Assert.Equal(((object[]) manager.GetJobForStratum().GetJobParams(true))[6].ToString(), notify["params"][6].Value<string>());
        else
            AssertNotifyDifficulty(notify, 1e-9);
        Assert.True(wire.Connection.Context.IsSubscribed);
        Assert.Null(wire.Connection.Context.UserAgent);
        Assert.Equal(1, wire.JobsCreated);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public async Task DateScalars_PreserveAuthorizationAndSubscribeCompatibilityAcrossCultures(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var (config, manager, clock, bus) = Fixture();
            await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
                budgetTimeProvider: new ManualTimeProvider());
            Assert.NotNull((await wire.RequestAsync("mining.subscribe", "2026-09-16T00:00:00Z"))["result"]);
            await Assignment(wire, 1e-9);
            Assert.Equal("09/16/2026 00:00:00", wire.Connection.Context.UserAgent);
            // Both consumed authorization positions contain Date tokens. They
            // remain ordinary authorization and do not consume difficulty allowance.
            for(var i = 0; i < 20; i++)
                Assert.True((await wire.RequestAsync("mining.authorize", "2026-09-16T00:00:00Z", "2026-09-17T00:00:00Z"))["result"].Value<bool>());
            Assert.Equal("09/16/2026 00:00:00", wire.Connection.Context.Miner);
            Assert.Equal(20, manager.AddressValidations);
            for(var i = 0; i < Miningcore.Blockchain.BitcoinBlake2b.DifficultyRequestBudget.Capacity; i++)
                await Accepted(wire, true, (i + 2) / 1e9);
            await Refused(wire, true);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }
}
