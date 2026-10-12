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

    private (PoolConfig Config, JobManager Manager, IMasterClock Clock, IMessageBus Bus) Fixture(bool merged)
    {
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var config = new PoolConfig
        {
            Id = "minimum-" + Guid.NewGuid().ToString("N"), Coin = "bitcoin",
            Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { new DaemonEndpointConfig() },
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
        internal void Seed(bool merged)
        {
            network = Network.RegTest;
            poolAddressDestination = new KeyId(new byte[20]);
            var template = new BlockTemplate
            {
                Height = 101, Version = 0x20000000, CurTime = 1_700_000_000,
                PreviousBlockhash = new string('0', 64), CoinbaseValue = 5_000_000_000,
                Bits = "207fffff", Target = "7" + new string('f', 63),
                Transactions = Array.Empty<BitcoinBlockTransaction>(),
            };
            if(merged)
            {
                var job = new MergedMiningBitcoinJob();
                job.InitMerged(template, new AuxBlockTemplate
                    { Hash = new string('1', 64), Bits = "207fffff", Height = 101 },
                    "template", poolConfig, extraPoolConfig, clusterConfig, clock,
                    poolAddressDestination, network, false, coin.ShareMultiplier,
                    coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);
                currentJob = job;
            }
            else
            {
                currentJob = new BitcoinJob();
                currentJob.Init(template, "template", poolConfig, extraPoolConfig, clusterConfig, clock,
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
    public async Task MaximumRepresentableMinimum_AcceptsTargetOneAndRejectsNextValue(bool merged)
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
        Assert.False((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
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
        foreach(var value in new object[] { 0, -1, 0.5, 1, "NaN", "Infinity", "1e400", "1,5", true, null,
            new object[] { 3 }, new { value = 3 }, 1e100 })
        {
            time.AdvanceMonotonic(TimeSpan.FromSeconds(10));
            await wire.SendRequestAsync("mining.configure", Minimum(value));
            Assert.False((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
            await Fence(wire);
            Assert.Equal(1, context.Difficulty);
            Assert.Same(dynamic, context.VarDiff);
            Assert.True(context.HasPendingDifficulty);
            Assert.Equal(jobs, context.validJobs.ToArray());
        }
        await wire.SendRequestAsync("mining.configure", new[] { "minimum-difficulty" }, new Dictionary<string, object>());
        Assert.False((await wire.ReadAsync())["result"]["minimum-difficulty"].Value<bool>());
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
