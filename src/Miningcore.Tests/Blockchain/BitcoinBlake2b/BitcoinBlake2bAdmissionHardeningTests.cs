using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
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
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureMutation_SerializesBroadcastAndImmediateVarDiff(bool immediateVarDiff)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        Assert.True((await wire.RequestAsync("mining.authorize", "test.worker", "ordinary"))["result"].Value<bool>());
        var mutated = Signal();
        var release = Signal();
        var waiting = Signal();
        wire.AfterConfigure = async () =>
        {
            wire.AfterConfigure = null;
            mutated.TrySetResult();
            await release.Task.WaitAsync(BarrierTimeout);
        };
        wire.AssignmentWaiting = () => waiting.TrySetResult();
        Task competing = null;
        try
        {
            await Send(wire, true, 3e-9);
            await mutated.Task.WaitAsync(BarrierTimeout);
            Assert.Equal(3e-9, wire.Connection.Context.Difficulty);
            Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
            competing = immediateVarDiff ? wire.UpdateVarDiffAsync(4e-9) :
                wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false));
            // Signaled only after WaitAsync encounters an already-held assignment gate.
            await waiting.Task.WaitAsync(BarrierTimeout);
            Assert.False(competing.IsCompleted);
            Assert.Equal(1, wire.JobsCreated);
            Assert.Equal(3e-9, wire.Connection.Context.Difficulty);
        }
        finally { release.TrySetResult(); }
        await competing.WaitAsync(BarrierTimeout);
        await Assignment(wire, 3e-9);
        if(immediateVarDiff)
            await Assignment(wire, 4e-9);
        else
            AssertNotifyDifficulty(await wire.ReadAsync(), 3e-9);
        await Fence(wire);
    }

    [Theory]
    [InlineData("mining.configure")]
    [InlineData("mining.suggest_difficulty")]
    [InlineData("mining.authorize")]
    public async Task PendingVarDiffAnnouncement_CannotSnapshotConcurrentMinerDifficulty(string method)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        Assert.True((await wire.RequestAsync("mining.authorize", "test.worker", "ordinary"))["result"].Value<bool>());
        var snapshotReached = Signal();
        var waiting = Signal();
        using var release = new ManualResetEventSlim();
        wire.BeforeCreateJob = () =>
        {
            wire.BeforeCreateJob = null;
            snapshotReached.TrySetResult();
            Assert.True(release.Wait(BarrierTimeout));
        };
        wire.AssignmentWaiting = () => waiting.TrySetResult();
        wire.Connection.Context.EnqueueNewDifficulty(2e-9);
        var broadcast = Task.Run(() => wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false)));
        try
        {
            await snapshotReached.Task.WaitAsync(BarrierTimeout);
            var announcement = await wire.ReadAsync();
            Assert.Equal("mining.set_difficulty", announcement["method"].Value<string>());
            Assert.Equal(2e-9, announcement["params"][0].Value<double>());
            if(method == "mining.authorize")
                await wire.SendRequestAsync(method, "test.worker", Password(4e-9));
            else
                await Send(wire, method == "mining.configure", 4e-9);
            await waiting.Task.WaitAsync(BarrierTimeout);
            Assert.Equal(2e-9, wire.Connection.Context.Difficulty);
        }
        finally { release.Set(); }
        await broadcast.WaitAsync(BarrierTimeout);
        // Authorize may acknowledge before entering its assignment gate. Every
        // notify must still match the most recently announced wire difficulty.
        var latestDifficulty = 2e-9;
        var snapshots = new List<double>();
        var responses = 0;
        for(var i = 0; i < 4; i++)
        {
            var message = await wire.ReadAsync();
            switch(message["method"]?.Value<string>())
            {
                case "mining.set_difficulty":
                    latestDifficulty = message["params"][0].Value<double>();
                    break;
                case "mining.notify":
                    AssertNotifyDifficulty(message, latestDifficulty);
                    snapshots.Add(latestDifficulty);
                    break;
                default:
                    Assert.Null(message["error"]);
                    responses++;
                    break;
            }
        }
        Assert.Equal(1, responses);
        Assert.Equal(new[] { 2e-9, 4e-9 }, snapshots);
        await Fence(wire);
    }

    [Fact]
    public async Task AuthorizationRpc_DoesNotHoldAssignmentGate()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        Assert.True((await wire.RequestAsync("mining.authorize", "test.worker", "ordinary"))["result"].Value<bool>());
        var validating = Signal();
        var release = Signal();
        manager.BeforeValidation = async () =>
        {
            validating.TrySetResult();
            await release.Task.WaitAsync(BarrierTimeout);
        };
        try
        {
            await wire.SendRequestAsync("mining.authorize", "test.worker", Password(4e-9));
            await validating.Task.WaitAsync(BarrierTimeout);
            await wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false)).WaitAsync(BarrierTimeout);
            AssertNotifyDifficulty(await wire.ReadAsync(), 1e-9);
        }
        finally { release.TrySetResult(); }
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await Assignment(wire, 4e-9);
        await Fence(wire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2e-9)]
    public async Task SubscribeNicehashLookup_DoesNotHoldAssignmentGateOrPublishPartialState(double? difficulty)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        Assert.True((await wire.RequestAsync("mining.authorize", "test.worker", "ordinary"))["result"].Value<bool>());
        var lookupStarted = Signal();
        var release = Signal();
        var lookups = 0;
        wire.NicehashLookup = async _ =>
        {
            lookups++;
            lookupStarted.TrySetResult();
            await release.Task.WaitAsync(BarrierTimeout);
            return difficulty;
        };
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var extraNonce = context.ExtraNonce1;
        try
        {
            await wire.SendRequestAsync("mining.subscribe", "NiceHash/1.0");
            await lookupStarted.Task.WaitAsync(BarrierTimeout);
            await wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false)).WaitAsync(BarrierTimeout);
            Assert.False(context.IsSubscribed);
            Assert.Null(context.UserAgent);
            Assert.Equal(extraNonce, context.ExtraNonce1);
            Assert.Equal(0, wire.JobsCreated);
        }
        finally { release.TrySetResult(); }
        Assert.NotNull((await wire.ReadAsync())["result"]);
        await Assignment(wire, difficulty ?? 1e-9);
        Assert.True(context.IsSubscribed);
        Assert.True(context.IsNicehash);
        Assert.Equal(1, lookups);
        Assert.Equal(1, wire.JobsCreated);
        await Fence(wire);
    }

    [Theory]
    [InlineData("test.worker", "test")]
    [InlineData(123, "123")]
    [InlineData(true, "True")]
    public async Task AuthorizationScalarsAndTrailingFields_PreserveCompatibilityAndBudget(object worker, string miner)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        await wire.SendRequestAsync("mining.authorize", worker, Password(2e-9), new JObject(), new JArray());
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await Assignment(wire, 2e-9);
        Assert.Equal(miner, wire.Connection.Context.Miner);
        Assert.Equal(1, manager.AddressValidations);
        for(var i = 0; i < DifficultyRequestBudget.Capacity - 1; i++)
            await Accepted(wire, true, (i + 3) / 1e9);
        await Refused(wire, true);
        Assert.True((await wire.RequestAsync("mining.authorize", worker, "ordinary", new JObject()))["result"].Value<bool>());
        Assert.Equal(2, manager.AddressValidations);
    }

    [Fact]
    public async Task ConfigureTrailingFields_PreserveCompatibilityAndBudget()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = "2e-9" }, new JObject(), new JArray());
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Assignment(wire, 2e-9);
        for(var i = 0; i < DifficultyRequestBudget.Capacity - 1; i++)
            await Accepted(wire, true, (i + 3) / 1e9);
        await Refused(wire, true);
    }

    [Theory]
    [InlineData(StratumError.Other)]
    [InlineData(StratumError.JobNotFound)]
    public async Task ConfigureProtocolError_ReleasesGateAndKeepsConnectionUsable(StratumError code)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus);
        await Subscribe(wire);
        wire.BeforeConfigure = () => throw new StratumException(code, "configure declined");
        await Send(wire, true, 2e-9);
        var response = await wire.ReadAsync();
        Assert.Equal((int) code, response["error"]["code"].Value<int>());
        Assert.Equal("configure declined", response["error"]["message"].Value<string>());
        Assert.False(response["result"].Value<bool>());
        Assert.Equal(1e-9, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
        wire.BeforeConfigure = null;
        await Accepted(wire, true, 2e-9);
        await Fence(wire);
    }

    [Fact]
    public async Task CanonicalConfigureMissingMinimumDifficulty_DeclinesWithoutDisconnect()
    {
        var (config, manager, clock, bus) = Fixture();
        config.Template = ModuleInitializer.CoinTemplates["bitcoin"];
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        var response = await wire.RequestAsync("mining.configure", new[] { "minimum-difficulty" }, new JObject());
        Assert.False(response["result"]["minimum-difficulty"].Value<bool>());
        Assert.Equal(1e-9, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
        response = await wire.RequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 2e-9 });
        Assert.True(response["result"]["minimum-difficulty"].Value<bool>());
        Assert.Equal(2e-9, wire.Connection.Context.Difficulty);
        Assert.True(wire.Connection.IsAlive);
    }

    private static void AssertNotifyDifficulty(JObject message, double difficulty)
    {
        Assert.Equal("mining.notify", message["method"].Value<string>());
        Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(
            BitcoinBlake2bHeader.TargetForDifficulty(difficulty)).ToString("x8"), message["params"][6].Value<string>());
    }

    [Theory]
    [InlineData("0.000000002")]
    [InlineData("2e-9")]
    [InlineData("  2e-9  ")]
    public async Task NumericStringConfigure_PreservesCompatibilityAndConsumesBudget(string value)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = value });
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Assignment(wire, 2e-9);
        for(var i = 0; i < DifficultyRequestBudget.Capacity - 1; i++)
            await Accepted(wire, true, (i + 3) / 1e9);
        await Refused(wire, true);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{},\"password\"]")]
    [InlineData("[[],\"password\"]")]
    [InlineData("[\"test.worker\",{}]")]
    [InlineData("[\"test.worker\",[]]")]
    public async Task MalformedAuthorization_IsBoundedAndDoesNotReachRpcOrMutateState(string parameters)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        for(var i = 0; i < DifficultyRequestBudget.Capacity + DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
        {
            await wire.SendRawAsync($"{{\"id\":100,\"method\":\"mining.authorize\",\"params\":{parameters}}}");
            var response = await wire.ReadAsync();
            Assert.Equal((int) StratumError.Other, response["error"]["code"].Value<int>());
            Assert.False(response["result"].Value<bool>());
            Assert.Equal(i < DifficultyRequestBudget.Capacity ? "Invalid request parameters" : RetryMessage(),
                response["error"]["message"].Value<string>());
        }
        Assert.Equal(0, manager.AddressValidations);
        Assert.False(wire.Connection.Context.IsAuthorized);
        Assert.Null(wire.Connection.Context.Miner);
        Assert.Equal(1e-9, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
        await Fence(wire);
        await wire.SendRawAsync($"{{\"id\":101,\"method\":\"mining.authorize\",\"params\":{parameters}}}");
        await wire.AssertNoMoreMessagesAsync();
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("-1")]
    [InlineData("2,0")]
    public async Task MalformedNumericStrings_ConsumeBudgetThenDisconnect(string value)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            budgetTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        for(var i = 0; i < DifficultyRequestBudget.Capacity + DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
        {
            await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
                new Dictionary<string, object> { ["minimum-difficulty.value"] = value });
            var response = await wire.ReadAsync();
            Assert.Equal((int) StratumError.Other, response["error"]["code"].Value<int>());
            Assert.Equal(i < DifficultyRequestBudget.Capacity ? "Invalid request parameters" : RetryMessage(),
                response["error"]["message"].Value<string>());
        }
        Assert.Equal(1e-9, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = value });
        await wire.AssertNoMoreMessagesAsync();
    }

    [Fact]
    public async Task MalformedNumericStrings_AfterCultureScenarios_KeepAdmissionContract()
    {
        // Explicit order in one execution context: runner ordering between
        // separate theory rows cannot establish that cultures were restored.
        var original = CultureInfo.CurrentCulture;
        foreach(var culture in new[] { "fr-FR", "tr-TR" })
        {
            await DateScalars_PreserveAuthorizationAndSubscribeCompatibilityAcrossCultures(culture);
            Assert.Equal(original, CultureInfo.CurrentCulture);
        }
        await SuggestAndConfigure_UseInvariantNumbersAndStringsAcrossCultures("de-DE");
        Assert.Equal(original, CultureInfo.CurrentCulture);
        await CanonicalSuggestion_RetainsItsLegacyCultureConversion();
        Assert.Equal(original, CultureInfo.CurrentCulture);
        foreach(var value in new[] { "NaN", "Infinity", "1e999", "-1", "2,0" })
            await MalformedNumericStrings_ConsumeBudgetThenDisconnect(value);

        // Even an explicitly inherited comma-decimal culture must not change
        // malformed-wire admission, its exact budget or terminal disconnect.
        try
        {
            foreach(var culture in new[] { "fr-FR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                await MalformedNumericStrings_ConsumeBudgetThenDisconnect("2,0");
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
