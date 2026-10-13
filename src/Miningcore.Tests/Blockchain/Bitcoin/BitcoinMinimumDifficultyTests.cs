using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinMinimumDifficultyTests : TestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static object[] Minimum(object value, params string[] extensions) => new object[]
    {
        extensions.Length == 0 ? new[] { "minimum-difficulty" } : extensions,
        new Dictionary<string, object> { ["minimum-difficulty.value"] = value },
    };

    private (PoolConfig Config, JobManager Manager, IMasterClock Clock, IMessageBus Bus) Fixture(bool merged, bool direct = false)
    {
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var config = new PoolConfig
        {
            Id = "minimum-" + Guid.NewGuid().ToString("N"), Coin = "bitcoin",
            Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { new DaemonEndpointConfig() },
            Extra = new Dictionary<string, object> { ["soloCoinbasePayout"] = direct },
        };
        var manager = new JobManager(container, clock, bus);
        manager.Configure(config, new ClusterConfig());
        manager.Seed(merged);
        return (config, manager, clock, bus);
    }

    private sealed class JobManager : BitcoinJobManager
    {
        internal JobManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus) :
            base(ctx, clock, bus, new BitcoinExtraNonceProvider("minimum", null)) { }
        internal Action BeforeJob;
        private int templates;
        internal void Seed(bool merged, string parentBits = "207fffff", string auxiliaryBits = "207fffff")
        {
            network = Network.RegTest;
            var jobId = $"template-{++templates}";
            poolAddressDestination = new KeyId(new byte[20]);
            var template = new BlockTemplate
            {
                Height = 101, Version = 0x20000000, CurTime = 1_700_000_000,
                PreviousBlockhash = new string('0', 64), CoinbaseValue = 5_000_000_000,
                Bits = parentBits, Target = new Target(Convert.ToUInt32(parentBits, 16)).ToUInt256().ToString(),
                Transactions = Array.Empty<BitcoinBlockTransaction>(),
            };
            if(merged)
            {
                var job = new MergedMiningBitcoinJob();
                job.InitMerged(template, new AuxBlockTemplate
                    { Hash = new string('1', 64), Bits = auxiliaryBits, Height = 101 },
                    jobId, poolConfig, extraPoolConfig, clusterConfig, clock,
                    poolAddressDestination, network, false, coin.ShareMultiplier,
                    coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);
                currentJob = job;
            }
            else
            {
                currentJob = new BitcoinJob();
                currentJob.Init(template, jobId, poolConfig, extraPoolConfig, clusterConfig, clock,
                    poolAddressDestination, network, false, coin.ShareMultiplier,
                    coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);
            }
        }
        public override BitcoinJob GetJobForStratum()
        {
            BeforeJob?.Invoke();
            return base.GetJobForStratum();
        }
        public override Task<bool> ValidateAddressAsync(string address, CancellationToken ct) => Task.FromResult(true);
    }

    private static async Task<JObject> Assignment(BitcoinBlake2bWireSession wire, double difficulty)
    {
        var set = await wire.ReadAsync();
        Assert.Equal("mining.set_difficulty", set["method"].Value<string>());
        Assert.Equal(difficulty, set["params"][0].Value<double>());
        var notify = await wire.ReadAsync();
        Assert.Equal("mining.notify", notify["method"].Value<string>());
        var job = wire.Connection.ContextAs<BitcoinWorkerContext>().GetJob(notify["params"][0].Value<string>());
        Assert.NotNull(job);
        Assert.True(JToken.DeepEquals(notify["params"], JArray.FromObject(job.GetJobParams(notify["params"][8].Value<bool>()))));
        return notify;
    }

    private static async Task<JObject> Subscribe(BitcoinBlake2bWireSession wire, double difficulty = 1)
    {
        await wire.SendRequestAsync("mining.subscribe", "minimum-test");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        return await Assignment(wire, difficulty);
    }

    private static async Task Fence(BitcoinBlake2bWireSession wire)
    {
        await wire.SendRequestAsync("mining.extranonce.subscribe");
        var response = await wire.ReadAsync();
        Assert.Null(response["method"]);
        Assert.True(response["result"].Value<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedMinimum_PublishesOneCoherentUsableAssignmentAndClearsDeferredWork(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1,
            varDiff: new VarDiffConfig { MinDiff = 1, MaxDiff = 2, TargetTime = 10, RetargetTime = 1 },
            varDiffTimeProvider: time);
        var original = await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.EnqueueNewDifficulty(2);
        await wire.SendRequestAsync("mining.configure", Minimum("3.0", "unknown", "minimum-difficulty", "minimum-difficulty"));
        var response = await wire.ReadAsync();
        Assert.Null(response["method"]);
        Assert.True(response["result"]["minimum-difficulty"].Value<bool>());
        Assert.False(response["result"]["unknown"].Value<bool>());
        var replacement = await Assignment(wire, 3);
        Assert.False(replacement["params"][8].Value<bool>());
        Assert.NotEqual(original["params"][0], replacement["params"][0]);
        Assert.NotNull(context.GetJob(original["params"][0].Value<string>()));
        Assert.Null(context.VarDiff);
        Assert.False(context.HasPendingDifficulty);
        Assert.Equal(3, context.AnnouncedDifficulty);
        await Fence(wire);
        await wire.SendRequestAsync("mining.configure", Minimum(3));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Fence(wire);
        Assert.Equal(2, wire.JobsCreated);
        await wire.RetargetVarDiffAsync(false);
        await wire.RetargetVarDiffAsync(true);
        Assert.False(context.HasPendingDifficulty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreSubscriptionMinimum_IsPublishedOnlyWhenSubscriptionIssuesWork(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await wire.SendRequestAsync("mining.configure", Minimum(3));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Fence(wire);
        Assert.Equal(0, wire.JobsCreated);
        Assert.Null(wire.Connection.ContextAs<BitcoinWorkerContext>().AnnouncedDifficulty);
        await Subscribe(wire, 3);
        Assert.Equal(1, wire.JobsCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaximumTargetRepresentation_AcceptsTargetOneAndRejectsNextValue(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await Subscribe(wire);
        var maximum = (double) BitcoinConstants.Diff1;
        await wire.SendRequestAsync("mining.configure", Minimum(maximum));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Assignment(wire, maximum);
        await wire.SendRequestAsync("mining.configure", Minimum(double.BitIncrement(maximum)));
        Assert.Equal(JTokenType.String, (await wire.ReadAsync())["result"]["minimum-difficulty"].Type);
        await Fence(wire);
        Assert.Equal(maximum, wire.Connection.Context.Difficulty);
        Assert.Equal(2, wire.JobsCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedMinimumAndOtherExtensions_DoNotMutateOrPublishWork(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1, varDiffTimeProvider: time,
            varDiff: new VarDiffConfig { MinDiff = 1, MaxDiff = 4, TargetTime = 10, RetargetTime = 1 });
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var dynamic = context.VarDiff;
        context.EnqueueNewDifficulty(2);
        var jobs = context.validJobs.ToArray();
        foreach(var value in new object[] { 0, -1, 0.5, "NaN", "Infinity", "1e400", "1,5", true, null,
            new object[] { 3 }, new { value = 3 }, 1e100 })
        {
            time.AdvanceMonotonic(TimeSpan.FromSeconds(10));
            await wire.SendRequestAsync("mining.configure", Minimum(value));
            Assert.Equal(JTokenType.String, (await wire.ReadAsync())["result"]["minimum-difficulty"].Type);
            await Fence(wire);
            Assert.Equal(1, context.Difficulty);
            Assert.Same(dynamic, context.VarDiff);
            Assert.True(context.HasPendingDifficulty);
            Assert.Equal(jobs, context.validJobs.ToArray());
        }
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" }, new Dictionary<string, object>());
        Assert.Equal(JTokenType.String, (await wire.ReadAsync())["result"]["minimum-difficulty"].Type);
        await Fence(wire);
        await wire.SendRequestAsync("mining.configure", new[] { "version-rolling", "unknown" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 3 });
        var result = (await wire.ReadAsync())["result"];
        Assert.True(result["version-rolling"].Value<bool>());
        Assert.False(result["unknown"].Value<bool>());
        await Fence(wire);
        Assert.Equal(1, wire.JobsCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureConstruction_SerializesBroadcastAndCancelsDeferredVarDiff(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1,
            varDiff: new VarDiffConfig { MinDiff = 1, MaxDiff = 4, TargetTime = 10, RetargetTime = 1 },
            varDiffTimeProvider: time);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.IsAuthorized = true;
        context.EnqueueNewDifficulty(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.BeforeJob = () =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        };
        await wire.SendRequestAsync("mining.configure", Minimum(3));
        Task broadcast = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.Equal(0, context.AssignmentGate.CurrentCount);
            Assert.Null(context.VarDiff);
            Assert.False(context.HasPendingDifficulty);
            Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
            Assert.Equal(3, (await wire.ReadAsync())["params"][0].Value<double>());
            broadcast = wire.AnnounceJobAsync(new object[] { "template", false });
            Assert.False(broadcast.IsCompleted);
            await wire.RetargetVarDiffAsync(true); // Disabled idle producer must not acquire or revive an assignment.
        }
        finally { release.TrySetResult(); }
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        await broadcast.WaitAsync(Timeout);
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
        await Fence(wire);
        Assert.Equal(3, context.Difficulty);
        Assert.Equal(3, context.AnnouncedDifficulty);
        Assert.False(context.HasPendingDifficulty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedEnvelope_IsRecoverableAndCannotPartiallyNegotiate(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await Subscribe(wire);
        foreach(var shape in new[] { "null", "{}", "4", "[]", "[[]]", "[null,{}]", "[[1],{}]",
            "[[\"\"],{}]", "[[\"version-rolling\"],null]", "[[\"version-rolling\"],[]]",
            "[[\"version-rolling\",\"version-rolling.mask\"],{}]",
            "[[\"version-rolling.mask\",\"version-rolling\"],{}]" })
        {
            await wire.SendRawAsync("{\"id\":42,\"method\":\"mining.configure\",\"params\":" + shape + "}");
            Assert.Equal((int) StratumError.Other, (await wire.ReadAsync())["error"]["code"].Value<int>());
            await Fence(wire);
            Assert.Null(wire.Connection.ContextAs<BitcoinWorkerContext>().VersionRollingMask);
        }
        await wire.SendRawAsync("{\"id\":null,\"method\":\"mining.configure\",\"params\":[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":3}]}");
        Assert.Equal((int) StratumError.MinusOne, (await wire.ReadAsync())["error"]["code"].Value<int>());
        await Fence(wire);
        Assert.Equal(1, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegotiatedMinimum_SurvivesSuggestionsAndCanBeExplicitlyReplaced(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await Subscribe(wire);
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Assignment(wire, 8);
        await wire.SendRequestAsync("mining.suggest_difficulty", 2);
        Assert.NotNull((await wire.ReadAsync())["error"]);
        await Fence(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        Assert.Equal(8, context.NegotiatedMinimumDifficulty);
        Assert.Equal(8, context.Difficulty);
        await wire.SendRequestAsync("mining.configure", Minimum(4));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Assignment(wire, 4);
        Assert.Equal(4, context.NegotiatedMinimumDifficulty);
        await wire.SendRequestAsync("mining.suggest_difficulty", 6);
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        await Assignment(wire, 6);
        Assert.Equal(4, context.NegotiatedMinimumDifficulty);
        await Fence(wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreSubscriptionMinimum_SurvivesLowerNiceHashAutoDifficulty(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        wire.NicehashLookup = _ => Task.FromResult<double?>(2);
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await wire.SendRequestAsync("mining.subscribe", "NiceHash/1.0");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        await Assignment(wire, 8);
        await Fence(wire);
        Assert.Equal(8, wire.Connection.ContextAs<BitcoinWorkerContext>().NegotiatedMinimumDifficulty);
        Assert.Equal(8, wire.Connection.Context.Difficulty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSoloMinimum_PreservesAuthorizationAwareWorkBoundary(bool authorized)
    {
        var (config, manager, clock, bus) = Fixture(false, direct: true);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1);
        await wire.SendRequestAsync("mining.subscribe", "direct-minimum");
        Assert.NotNull((await wire.ReadAsync())["result"]);
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"].Value<string>());
        await Fence(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        BitcoinWorkerContext.DirectPayoutAuthorization authorization = null;
        if(authorized)
        {
            var destination = new KeyId(new byte[20]).GetAddress(Network.RegTest);
            authorization = context.SetDirectPayoutAuthorization(destination.ToString(), destination);
            context.IsAuthorized = true;
        }
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        if(authorized)
        {
            var notice = await Assignment(wire, 8);
            var job = context.GetJob(notice["params"][0].Value<string>());
            Assert.Equal(authorization.Generation, job.DirectPayoutGeneration);
            Assert.Equal(authorization.Address, job.DirectPayoutAddress);
        }
        else
        {
            Assert.Equal(8, (await wire.ReadAsync())["params"][0].Value<double>());
            Assert.Empty(context.validJobs);
        }
        await Fence(wire);
        Assert.Equal(authorized ? 1 : 0, wire.JobsCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetRefusal_DefinesEveryExtensionWithoutMutationAndNullIdsSpendNothing(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1, varDiffTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        for(var i = 0; i < 10; i++)
        {
            await wire.SendRawAsync("{\"id\":null,\"method\":\"mining.configure\",\"params\":[[\"minimum-difficulty\"],{\"minimum-difficulty.value\":8}]}");
            Assert.NotNull((await wire.ReadAsync())["error"]);
        }
        for(var i = 0; i < Miningcore.Blockchain.BitcoinBlake2b.DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRequestAsync("mining.configure", Minimum(0));
            Assert.Equal(JTokenType.String, (await wire.ReadAsync())["result"]["minimum-difficulty"].Type);
        }
        await wire.SendRequestAsync("mining.configure", Minimum(8, "minimum-difficulty", "version-rolling", "unknown"));
        var refused = await wire.ReadAsync();
        Assert.Null(refused["error"]);
        Assert.Equal(JTokenType.String, refused["result"]["minimum-difficulty"].Type);
        Assert.Equal(JTokenType.String, refused["result"]["version-rolling"].Type);
        Assert.False(refused["result"]["unknown"].Value<bool>());
        Assert.Null(wire.Connection.ContextAs<BitcoinWorkerContext>().VersionRollingMask);
        Assert.Equal(1, wire.Connection.Context.Difficulty);
        await Fence(wire);
        await wire.SendRequestAsync("mining.configure", Enumerable.Range(0, 33).Select(i => "unknown-" + i).ToArray(), new { });
        Assert.NotNull((await wire.ReadAsync())["error"]);
        await Fence(wire);
    }

    [Theory]
    [InlineData(false, "options")]
    [InlineData(true, "options")]
    [InlineData(false, "reserved")]
    [InlineData(true, "reserved")]
    [InlineData(false, "limit")]
    [InlineData(true, "limit")]
    public async Task MalformedMinimumEnvelope_ConsumesAdmissionAndEventuallyClosesSession(bool merged, string shape)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1, varDiffTimeProvider: new ManualTimeProvider());
        await Subscribe(wire);
        var names = shape == "reserved" ? new[] { "minimum-difficulty", "version-rolling.mask" }
            : shape == "limit" ? Enumerable.Repeat("minimum-difficulty", 33).ToArray() : new[] { "minimum-difficulty" };
        object values = shape == "options" ? new JArray() : new JObject();
        for(var i = 0; i < Miningcore.Blockchain.BitcoinBlake2b.DifficultyRequestBudget.Capacity; i++)
        {
            await wire.SendRequestAsync("mining.configure", names, values);
            Assert.NotNull((await wire.ReadAsync())["error"]);
        }
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        Assert.Equal("Too many difficulty requests; retry later", (await wire.ReadAsync())["result"]["minimum-difficulty"].Value<string>());
        for(var i = 1; i < Miningcore.Blockchain.BitcoinBlake2b.DifficultyRequestBudget.DisconnectAfterRefusals - 1; i++)
        {
            await wire.SendRequestAsync("mining.configure", names, values);
            Assert.Contains("Too many", (await wire.ReadAsync())["error"]["message"].Value<string>());
        }
        await wire.SendRequestAsync("mining.configure", names, values);
        await wire.AssertNoMoreMessagesAsync();
        Assert.True(wire.Connection.Context.DifficultyRequests.IsClosed);
        Assert.Null(wire.Connection.ContextAs<BitcoinWorkerContext>().NegotiatedMinimumDifficulty);
        Assert.Equal(1, wire.Connection.Context.Difficulty);
        Assert.Equal(1, wire.JobsCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortEqualMinimum_KeepsVarDiffAndRetargetsOnlyUpToEasiestCurrentChain(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        manager.Seed(merged, "1c0ffff0", "1d00ffff"); // parent 16, aux 1
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 0.25, varDiffTimeProvider: time,
            varDiff: new VarDiffConfig { MinDiff = 0.25, TargetTime = 1000, RetargetTime = 1, VariancePercent = 10 });
        await Subscribe(wire, 0.25);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var dynamic = context.VarDiff;
        context.EnqueueNewDifficulty(0.5);
        await wire.SendRequestAsync("mining.configure", Minimum(0.25));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Fence(wire);
        Assert.Same(dynamic, context.VarDiff);
        Assert.False(context.HasPendingDifficulty);
        Assert.Equal(1, wire.JobsCreated);
        await wire.RetargetVarDiffAsync(false);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await wire.RetargetVarDiffAsync(false);
        var ceiling = manager.EasiestNetworkDifficulty.Value;
        await Assignment(wire, ceiling);
        Assert.Equal(ceiling, context.Difficulty);
        await wire.SendRequestAsync("mining.configure", Minimum(0.5));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Fence(wire);
        Assert.Equal(ceiling, context.Difficulty);
        Assert.Same(dynamic, context.VarDiff); // A satisfied floor does not pin a faster miner.
        // Repeated retargets at the ceiling and floor do not republish work.
        var count = wire.JobsCreated;
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await wire.RetargetVarDiffAsync(false);
        await Fence(wire);
        Assert.Equal(count, wire.JobsCreated);
        await wire.UpdateVarDiffAsync(0.1);
        await Assignment(wire, 0.5);
        await wire.UpdateVarDiffAsync(0.1);
        await Fence(wire);
        Assert.Equal(count + 1, wire.JobsCreated);
        Assert.Equal(0.5, context.NegotiatedMinimumDifficulty);
        Assert.Same(dynamic, context.VarDiff);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FallingChainCeiling_BroadcastLowersOnTargetVarDiffBeforeIssuingWork(bool merged, bool pending)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        manager.Seed(merged, "1c0ffff0", "1c0ffff0");
        var time = new ManualTimeProvider();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: pending ? 0.25 : 8, varDiffTimeProvider: time,
            varDiff: new VarDiffConfig { MinDiff = 0.25, TargetTime = 10, RetargetTime = 1, VariancePercent = 10 });
        await Subscribe(wire, pending ? 0.25 : 8);
        await wire.SendRequestAsync("mining.authorize", "miner.worker", "x");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var old = Assert.Single(context.validJobs);
        await wire.RetargetVarDiffAsync(false);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        await wire.RetargetVarDiffAsync(false); // On-target: the periodic calculation does not retarget.
        await Fence(wire);
        if(pending) context.EnqueueNewDifficulty(8);
        manager.Seed(merged, merged ? "1c0ffff0" : "1d00ffff", "1d00ffff");
        await wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false));
        await Assignment(wire, 1);
        await Fence(wire);
        Assert.Equal(1, context.Difficulty);
        Assert.False(context.HasPendingDifficulty);
        Assert.NotNull(context.VarDiff);
        Assert.Contains(old, context.validJobs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IncompatibleFloor_BroadcastKeepsPolicyAndBoundsWarningsAcrossMovingChainTargets(bool merged, bool deviceFloor)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        manager.Seed(merged, "1c0ffff0", "1c0ffff0");
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 8,
            varDiff: new VarDiffConfig { MinDiff = deviceFloor ? 0.25 : 8, TargetTime = 10, RetargetTime = 1 });
        await Subscribe(wire, 8);
        await wire.SendRequestAsync("mining.authorize", "miner.worker", "x");
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        if(deviceFloor)
        {
            await wire.SendRequestAsync("mining.configure", Minimum(8));
            Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        }
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("incompatible-floor"));
        foreach(var bits in new[] { "1d00ffff", "1d01fffe", "1d03fffc" })
        {
            manager.Seed(merged, merged ? "1c0ffff0" : bits, bits);
            await wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false));
            Assert.Equal("mining.notify", (await wire.ReadAsync())["method"].Value<string>());
            await Fence(wire);
            Assert.Equal(8, context.Difficulty);
        }
        Assert.Single(target.Logs.Where(x => x.Contains("honest work can be under-credited")));
        Assert.Equal(deviceFloor ? 0 : 1, target.Logs.Count(x => x.Contains("VarDiff MinDiff")));
        Assert.NotNull(context.VarDiff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AboveNetworkMinimum_WarnsOnceAndRetainsExplicitDeviceConstraint(bool merged)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await Subscribe(wire);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("credit-warning"));
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Assignment(wire, 8);
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        Assert.True((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
        await Fence(wire);
        Assert.Single(target.Logs.Where(x => x.Contains("honest work can be under-credited")));
        Assert.Equal(8, wire.Connection.Context.Difficulty);
    }

    [Fact]
    public async Task SuppressedMutationCancellation_DoesNotMislabelLaterPublicationFailure()
    {
        var (config, manager, clock, bus) = Fixture(false);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1);
        await Subscribe(wire);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("cancel-label"));
        using var cancel = new CancellationTokenSource();
        wire.Connection.ContextAs<BitcoinWorkerContext>().ConfigureDifficultyPolicy(1, _ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wire.DispatchBufferedAsync(cancel.Token, "mining.configure", Minimum(8)));
        Assert.DoesNotContain(target.Logs, x => x.Contains("AssignmentMutationFailure"));
        wire.ClosePublicationFailure(new IOException("later-publication"));
        Assert.Single(target.Logs.Where(x => x.Contains("AssignmentPublicationFailure")));
        Assert.DoesNotContain(target.Logs, x => x.Contains("AssignmentMutationFailure"));
    }

    [Fact]
    public async Task PreResponseInvariantFailure_ClosesWorkAndReportsMutationWithoutReply()
    {
        var (config, manager, clock, bus) = Fixture(false);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: true, difficulty: 1);
        await Subscribe(wire);
        using var logs = new NLog.LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("pre-response"));
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.ConfigureDifficultyPolicy(1, _ => throw new InvalidOperationException("private-marker"));
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRequestAsync("mining.configure", Minimum(8));
        await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Empty(context.validJobs);
        Assert.Contains(target.Logs, x => x.Contains("AssignmentMutationFailure"));
        Assert.DoesNotContain(target.Logs, x => x.Contains("AssignmentPublicationFailure") || x.Contains("private-marker"));
        await wire.DispatchBufferedAsync("mining.extranonce.subscribe");
        Assert.Equal(responses, wire.Connection.ResponseSequence);
    }

    [Theory]
    [InlineData(false, 16)]
    [InlineData(false, 15)]
    [InlineData(false, 14)]
    [InlineData(true, 16)]
    [InlineData(true, 15)]
    [InlineData(true, 14)]
    public async Task ResponseDifficultyOrJobEnqueueFailure_IsTerminalWithoutSecondResponse(bool merged, int queued)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        var responses = wire.Connection.ResponseSequence;
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.Connection.SendMessageOverride = async (_, ct) =>
        {
            sending.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        };
        await wire.Connection.NotifyAsync("test.queue", Array.Empty<object>());
        await sending.Task.WaitAsync(Timeout);
        for(var i = 0; i < queued; i++)
            await wire.Connection.NotifyAsync("test.queue", Array.Empty<object>());
        await Assert.ThrowsAsync<IOException>(() => wire.DispatchBufferedAsync("mining.configure", Minimum(3)));
        await wire.AssertDisconnectedAsync();
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.Empty(context.validJobs);
        Assert.Throws<BitcoinJobRegistryClosedException>(() => context.AddJob(manager.GetJobForStratum(), 4));
        await wire.DispatchBufferedAsync("mining.configure", Minimum(4));
        await wire.DispatchBufferedAsync("mining.extranonce.subscribe");
        await wire.AnnounceJobAsync(manager.GetJobForStratum().GetJobParams(false));
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.Equal(3, context.Difficulty);
        Assert.Empty(context.validJobs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JobConstructionFailureOrCancellation_InvalidatesBeforeGateRelease(bool merged, bool cancellation)
    {
        var (config, manager, clock, bus) = Fixture(merged);
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus,
            canonical: !merged, merged: merged, difficulty: 1);
        await Subscribe(wire);
        using var cancel = new CancellationTokenSource();
        manager.BeforeJob = () =>
        {
            Assert.Equal(0, wire.Connection.Context.AssignmentGate.CurrentCount);
            if(cancellation)
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }
            throw new InvalidOperationException("construction failed");
        };
        var responses = wire.Connection.ResponseSequence;
        var error = await Record.ExceptionAsync(() => wire.DispatchBufferedAsync(cancel.Token, "mining.configure", Minimum(3)));
        Assert.IsAssignableFrom<Exception>(error);
        Assert.Equal(cancellation, error is OperationCanceledException);
        manager.BeforeJob = null;
        await wire.AssertDisconnectedAsync();
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        Assert.Equal(1, wire.Connection.Context.AssignmentGate.CurrentCount);
        await wire.DispatchBufferedAsync("mining.configure", Minimum(4));
        Assert.Equal(3, wire.Connection.Context.Difficulty);
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
    }
}
