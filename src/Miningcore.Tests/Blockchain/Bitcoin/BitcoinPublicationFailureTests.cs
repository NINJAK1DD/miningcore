using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Tests.Blockchain.BitcoinBlake2b;
using Miningcore.Time;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPublicationFailureTests : TestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Request(string method, object[] parameters = null, int id = 42) =>
        JsonConvert.SerializeObject(new { id, method, @params = parameters ?? Array.Empty<object>() });

    private static object[] Parameters(string method) => method switch
    {
        "mining.subscribe" => new object[] { "publication-test" },
        "mining.authorize" => new object[] { "miner.worker", "d=2" },
        "mining.configure" => new object[] { new[] { "minimum-difficulty" },
            new Dictionary<string, object> { ["minimum-difficulty.value"] = 2 } },
        "mining.suggest_difficulty" => new object[] { 2 },
        _ => Array.Empty<object>(),
    };

    private (PoolConfig Config, TestManager Manager, IMasterClock Clock, IMessageBus Bus) Fixture()
    {
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = Substitute.For<IMessageBus>();
        var config = new PoolConfig
        {
            Id = "publication-" + Guid.NewGuid().ToString("N"),
            Coin = "bitcoin",
            Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { new DaemonEndpointConfig() },
            Banning = new PoolShareBasedBanningConfig { Enabled = true, CheckThreshold = 1, InvalidPercent = 1 },
        };
        var manager = new TestManager(container, clock, bus);
        manager.Configure(config, new ClusterConfig());
        return (config, manager, clock, bus);
    }

    private sealed class TestManager : BitcoinJobManager
    {
        internal TestManager(IComponentContext ctx, IMasterClock clock, IMessageBus bus) :
            base(ctx, clock, bus, new BitcoinExtraNonceProvider("publication", null)) { }

        internal int Submissions;
        internal Share AcceptedShare;
        internal bool IsCandidate;
        internal Action BeforeGetJob;
        public override Task<bool> ValidateAddressAsync(string address, CancellationToken ct) => Task.FromResult(true);
        public override BitcoinJob GetJobForStratum()
        {
            BeforeGetJob?.Invoke();
            return new TestJob();
        }
        public override ValueTask<Share> SubmitShareAsync(StratumConnection connection, object submission, CancellationToken ct)
        {
            Submissions++;
            connection.ContextAs<BitcoinWorkerContext>().MarkProofAccepted();
            AcceptedShare = new Share
            {
                PoolId = poolConfig.Id, Miner = "immutable-miner", Worker = "immutable-worker", Difficulty = 1,
                IsBlockCandidate = IsCandidate,
            };
            return ValueTask.FromResult(AcceptedShare);
        }
    }

    private sealed class TestJob : BitcoinJob
    {
        public override object GetJobParams(bool clean) => new object[]
            { "test-job", new string('0', 64), "00", "00", Array.Empty<string>(), "20000000", "1d00ffff", "00000000", clean };
    }

    private async Task Subscribe(BitcoinBlake2bWireSession wire)
    {
        await wire.SendRawAsync(Request("mining.subscribe", Parameters("mining.subscribe")));
        Assert.NotNull((await wire.ReadAsync())["result"]);
        Assert.Equal("mining.set_difficulty", (await wire.ReadAsync())["method"]?.Value<string>());
        Assert.Equal("mining.notify", (await wire.ReadAsync())["method"]?.Value<string>());
    }

    [Theory]
    [InlineData("mining.subscribe", false)]
    [InlineData("mining.subscribe", true)]
    [InlineData("mining.authorize", false)]
    [InlineData("mining.authorize", true)]
    [InlineData("mining.configure", false)]
    [InlineData("mining.configure", true)]
    public async Task PostResponseFailure_IsTerminalWithOneAttempt(string method, bool unexpected)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Info, LogLevel.Fatal, target);
        logs.Configuration = logging;
        wire.SetLogger(logs.GetLogger("publication"));
        var entered = Barrier();
        var release = Barrier();
        async Task Fail()
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Timeout);
            throw unexpected ? new InvalidOperationException("sensitive-exception-marker") :
                new StratumException(StratumError.JobNotFound, "sensitive-exception-marker");
        }
        if(method == "mining.configure")
            wire.Canonical.AfterConfigure = Fail;
        else if(method == "mining.authorize")
            wire.Canonical.BeforeStaticDifficulty = Fail;
        else
        {
            // Subscription publishes its response and difficulty before job construction.
            wire.Canonical.BeforeCreateJob = () => throw (unexpected ?
                new InvalidOperationException("sensitive-exception-marker") :
                new StratumException(StratumError.JobNotFound, "sensitive-exception-marker"));
            wire.Canonical.BeforeSubscribe = async () =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Timeout);
            };
        }
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRawAsync(Request(method, Parameters(method)));
        await entered.Task.WaitAsync(Timeout);
        try
        {
            // Queue later requests while the failing handler demonstrably owns dispatch.
            await wire.SendRawAsync(Request("mining.extranonce.subscribe") + "\n" + Request("mining.subscribe"));
        }
        finally { release.TrySetResult(); }
        var messages = await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.InRange(messages.Count(x => x["id"]?.Value<int?>() == 42), 0, 1);
        Assert.DoesNotContain(messages, x => x["error"]?.Type is not (null or JTokenType.Null));
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        Assert.True(wire.Connection.IsDisconnectRequested);
        var diagnostic = Assert.Single(target.Logs.Where(x => x.Contains("AssignmentPublicationFailure")));
        Assert.DoesNotContain("sensitive-exception-marker", diagnostic);
        Assert.EndsWith("|", diagnostic);
        var record = JObject.Parse(diagnostic[diagnostic.IndexOf('{')..^1]);
        Assert.All(record.Properties(), x => Assert.Contains(x.Name, new[] { "event", "connectionId", "failure", "code" }));
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.StratumAdmission && x.Info == "publication-failure"), Arg.Any<string>());
        // Also reject a decoded request held outside the receive loop.
        await wire.DispatchBufferedAsync("mining.extranonce.subscribe");
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
    }

    [Fact]
    public async Task PreResponseRejection_CanRetryWithRepeatedId_ThenFailTerminally()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        wire.Canonical.BeforeSubscribe = () => throw new StratumException(StratumError.JobNotFound, "retry");
        await wire.SendRawAsync(Request("mining.subscribe"));
        Assert.Equal((int) StratumError.JobNotFound, (await wire.ReadAsync())["error"]["code"].Value<int>());
        Assert.Equal(1, wire.Connection.ResponseSequence);
        Assert.False(wire.Connection.Context.IsSubscribed);
        Assert.False(wire.Connection.IsDisconnectRequested);
        wire.Canonical.BeforeSubscribe = null;
        await Subscribe(wire); // deliberately reuses ID 42
        await wire.SendRawAsync(Request("mining.extranonce.subscribe"));
        Assert.True((await wire.ReadAsync())["result"].Value<bool>());
        Assert.Equal(3, wire.Connection.ResponseSequence);
        wire.Canonical.BeforeCreateJob = () => throw new StratumException(StratumError.JobNotFound, "later failure");
        await wire.SendRawAsync(Request("mining.subscribe"));
        await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(4, wire.Connection.ResponseSequence);
    }

    [Theory]
    [InlineData("mining.subscribe", 16)]
    [InlineData("mining.subscribe", 15)]
    [InlineData("mining.authorize", 16)]
    [InlineData("mining.authorize", 15)]
    [InlineData("mining.configure", 16)]
    [InlineData("mining.suggest_difficulty", 16)]
    [InlineData("mining.suggest_difficulty", 15)]
    public async Task FullSendQueue_ResponseOrNotificationFailure_NeverRetries(string method, int queued)
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        var sending = Barrier();
        wire.Connection.SendMessageOverride = async (_, ct) =>
        {
            sending.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        };
        await wire.Connection.NotifyAsync("test", Array.Empty<object>());
        await sending.Task.WaitAsync(Timeout);
        for(var i = 0; i < queued; i++)
            await wire.Connection.NotifyAsync("test", Array.Empty<object>());
        await wire.SendRawAsync(Request(method, Parameters(method)) + "\n" + Request("mining.extranonce.subscribe"));
        await wire.AssertDisconnectedAsync();
        Assert.Equal(1, wire.Connection.ResponseSequence);
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Fact]
    public async Task CompletedQueue_RecoveryResponseIsStillOneFailedAttempt()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        wire.Canonical.BeforeSubscribe = () =>
        {
            var queue = (BufferBlock<object>) typeof(StratumConnection)
                .GetField("sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wire.Connection);
            queue.Complete();
            throw new StratumException(StratumError.JobNotFound, "pre-response rejection");
        };
        await wire.SendRawAsync(Request("mining.subscribe"));
        await wire.AssertDisconnectedAsync();
        Assert.Equal(1, wire.Connection.ResponseSequence);
        Assert.True(wire.Connection.IsDisconnectRequested);
    }

    [Fact]
    public async Task AcceptedShare_VarDiffFailure_PublishesExactlyOnceWithoutInvalidShareOrBan()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        var context = wire.Connection.ContextAs<BitcoinWorkerContext>();
        context.IsAuthorized = true;
        context.Miner = "mutable-miner";
        var options = new VarDiffConfig { MinDiff = 1e-9, MaxDiff = 4e-9, TargetTime = 10, RetargetTime = 1, VariancePercent = 0 };
        config.Ports[wire.Connection.LocalEndpoint.Port].VarDiff = options;
        var now = new DateTimeOffset(clock.Now).ToUnixTimeMilliseconds() / 1000d;
        context.VarDiff = new VarDiffContext { Config = options, LastTs = now - 1, LastRetarget = now - 10 };
        wire.Canonical.BeforeCreateJob = () => throw new StratumException(StratumError.JobNotFound, "publication failure");
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRawAsync(Request("mining.submit") + "\n" + Request("mining.submit"));
        await wire.ReadUntilDisconnectedAsync();
        Assert.Equal(1, manager.Submissions);
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.Equal(1, context.Stats.ValidShares);
        Assert.Equal(0, context.Stats.InvalidShares);
        Assert.Equal("immutable-miner", manager.AcceptedShare.Miner);
        Assert.Empty(context.validJobs);
        bus.Received(1).SendMessage(Arg.Is<Share>(x => ReferenceEquals(x, manager.AcceptedShare)), Arg.Any<string>());
        bus.DidNotReceive().SendMessage(Arg.Is<TelemetryEvent>(x => x.Category == TelemetryCategory.Share && x.Success == false), Arg.Any<string>());
    }

    [Fact]
    public async Task AccountingFailureAfterProofValidation_DoesNotCountInvalidProof()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        wire.Connection.Context.IsAuthorized = true;
        bus.When(x => x.SendMessage(Arg.Any<Share>(), Arg.Any<string>()))
            .Do(_ => throw new StratumException(StratumError.Other, "accounting failure"));
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRawAsync(Request("mining.submit"));
        await wire.AssertDisconnectedAsync();
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Equal(0, wire.Connection.Context.Stats.InvalidShares);
        Assert.Equal(1, manager.Submissions);
    }

    [Fact]
    public async Task IdleVarDiffFailure_ClosesWithoutResponseAndCannotResume()
    {
        var (config, manager, clock, bus) = Fixture();
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        var responses = wire.Connection.ResponseSequence;
        wire.Canonical.BeforeCreateJob = () => throw new StratumException(StratumError.JobNotFound, "idle failure");
        await Assert.ThrowsAsync<StratumException>(() => wire.UpdateVarDiffAsync(2e-9));
        await wire.AssertDisconnectedAsync();
        await wire.DispatchBufferedAsync("mining.subscribe");
        await wire.UpdateVarDiffAsync(3e-9);
        Assert.Equal(responses, wire.Connection.ResponseSequence);
        Assert.Empty(wire.Connection.ContextAs<BitcoinWorkerContext>().validJobs);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x => x.Info == "publication-failure"), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedShare_FailedResponseEnqueue_DoesNotRepublishOrCountInvalid(bool candidate)
    {
        var (config, manager, clock, bus) = Fixture();
        manager.IsCandidate = candidate;
        await using var wire = new BitcoinBlake2bWireSession(container, clock, config, manager, bus, canonical: true);
        await Subscribe(wire);
        wire.Connection.Context.IsAuthorized = true;
        var sending = Barrier();
        wire.Connection.SendMessageOverride = async (_, ct) =>
        {
            sending.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        };
        await wire.Connection.NotifyAsync("test", Array.Empty<object>());
        await sending.Task.WaitAsync(Timeout);
        for(var i = 0; i < 16; i++)
            await wire.Connection.NotifyAsync("test", Array.Empty<object>());
        var responses = wire.Connection.ResponseSequence;
        await wire.SendRawAsync(Request("mining.submit") + "\n" + Request("mining.submit"));
        await wire.AssertDisconnectedAsync();
        Assert.Equal(responses + 1, wire.Connection.ResponseSequence);
        Assert.Equal(1, manager.Submissions);
        Assert.Equal(0, wire.Connection.Context.Stats.InvalidShares);
        Assert.Equal(1, wire.Connection.Context.Stats.ValidShares);
        Assert.Equal(candidate ? clock.Now : (DateTime?) null, wire.Canonical.LastPoolBlockTime);
        bus.Received(1).SendMessage(Arg.Is<TelemetryEvent>(x =>
            x.Category == TelemetryCategory.Share && x.Success == true), Arg.Any<string>());
        bus.Received(1).SendMessage(Arg.Any<Share>(), Arg.Any<string>());
    }
}
