using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using NSubstitute;
using NLog;
using Xunit;

namespace Miningcore.Tests.Mining;

public class PoolBaseVarDiffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IEnumerable<object[]> Races => VarDiffMonotonicTests.Families.SelectMany(family =>
        from duringCalculation in new[] { false, true }
        from replace in new[] { false, true }
        from deferred in new[] { false, true }
        select new object[] { family[0], family[1], duringCalculation, replace, deferred });

    [Theory]
    [MemberData(nameof(Races))]
    public async Task AllFamilies_ExplicitAssignmentWaitsForCalculationAndPublicationThenWins(
        string family, bool idle, bool duringCalculation, bool replace, bool deferred)
    {
        await using var fixture = new Fixture(family, deferred);
        var worker = fixture.Connection.Context;
        var original = worker.VarDiff;
        fixture.Time.Timestamp = 5 * TimeSpan.TicksPerSecond;
        using var calculationRelease = new ManualResetEventSlim();
        var reached = Signal();
        var publicationRelease = Signal();
        if(duringCalculation)
            fixture.Time.OnSample = () =>
            {
                Assert.True(Monitor.IsEntered(original)); // After the manager identity check.
                reached.TrySetResult();
                Assert.True(calculationRelease.Wait(Timeout));
            };
        else
            fixture.Pool.BeforePublication = async () =>
            {
                Assert.NotNull(original.LastUpdate); // Calculation returned; publication has not started.
                reached.TrySetResult();
                await publicationRelease.Task.WaitAsync(Timeout);
            };

        var waiting = Signal();
        fixture.Pool.AssignmentWaiting = () => waiting.TrySetResult();
        var update = Task.Run(() => fixture.Pool.Retarget(fixture.Connection, idle));
        Task fixedAssignment = null;
        try
        {
            await reached.Task.WaitAsync(Timeout);
            fixedAssignment = fixture.Pool.AssignExplicit(fixture.Connection, replace);
            await waiting.Task.WaitAsync(Timeout); // Actual semaphore contention, not a sleep.
            Assert.False(fixedAssignment.IsCompleted);
            Assert.Same(original, worker.VarDiff);
            Assert.Equal(10, worker.Difficulty);
        }
        finally
        {
            fixture.Time.OnSample = null;
            calculationRelease.Set();
            publicationRelease.TrySetResult();
        }
        await update.WaitAsync(Timeout);
        await fixedAssignment.WaitAsync(Timeout);
        Assert.Equal(55, worker.Difficulty);
        Assert.False(worker.HasPendingDifficulty);
        Assert.False(worker.ApplyPendingDifficulty()); // Deferred work cannot resurrect the old assignment.
        Assert.Equal(replace, worker.VarDiff != null);
        if(replace)
        {
            Assert.NotSame(original, worker.VarDiff);
            Assert.Equal(0, worker.VarDiff.CreatedTimestamp);
            Assert.Null(worker.VarDiff.LastShareTimestamp);
        }
        Assert.Equal(deferred ? new[] { 55d } : new[] { 20d, 55d }, fixture.Pool.Notifications);
        // A later sweep uses the authoritative context, never the old provider.
        await fixture.Pool.Retarget(fixture.Connection, true);
        Assert.Equal(55, worker.Difficulty);
        Assert.False(worker.HasPendingDifficulty);
        Assert.Equal(55, fixture.Pool.Notifications.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitAssignmentBeforeCalculation_ClearsPendingAndPreventsOldPublication(bool replace)
    {
        await using var fixture = new Fixture();
        var original = fixture.Connection.Context.VarDiff;
        fixture.Connection.Context.EnqueueNewDifficulty(20);
        await fixture.Pool.AssignExplicit(fixture.Connection, replace);
        fixture.Time.Timestamp = 30 * TimeSpan.TicksPerSecond;
        await fixture.Pool.Retarget(fixture.Connection, true);
        Assert.Null(original.LastUpdate);
        Assert.Equal(new[] { 55d }, fixture.Pool.Notifications);
        Assert.False(fixture.Connection.Context.HasPendingDifficulty);
    }

    [Fact]
    public async Task OnConnect_PassesTheProductionDefaultAndInjectedProviderThroughInit()
    {
        await using var fixture = new Fixture();
        Assert.Same(fixture.Time, fixture.Connection.Context.VarDiff.TimeProvider);
        fixture.Pool.VarDiffTimeProvider = TimeProvider.System;
        var before = TimeProvider.System.GetTimestamp();
        fixture.Pool.Connect(fixture.Connection, fixture.Connection.LocalEndpoint);
        var after = TimeProvider.System.GetTimestamp();
        Assert.Same(TimeProvider.System, fixture.Connection.Context.VarDiff.TimeProvider);
        Assert.InRange(fixture.Connection.Context.VarDiff.CreatedTimestamp, before, after);
    }

    [Fact]
    public async Task CancellationWhileWaitingForAssignment_DoesNotMutateAndReleasesNoUnownedGate()
    {
        await using var fixture = new Fixture();
        var context = fixture.Connection.Context;
        await context.AssignmentGate.WaitAsync();
        using var cancel = new CancellationTokenSource();
        var waiting = Signal();
        fixture.Pool.AssignmentWaiting = () => waiting.TrySetResult();
        try
        {
            var update = fixture.Pool.Retarget(fixture.Connection, false, cancel.Token);
            await waiting.Task.WaitAsync(Timeout);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            Assert.Equal(0, context.AssignmentGate.CurrentCount);
            Assert.Null(context.VarDiff.LastUpdate);
            Assert.False(context.HasPendingDifficulty);
        }
        finally { context.AssignmentGate.Release(); }
        await fixture.Pool.AssignExplicit(fixture.Connection, false).WaitAsync(Timeout);
    }

    [Fact]
    public async Task FailedPublication_ReleasesAssignmentGate()
    {
        await using var fixture = new Fixture();
        fixture.Time.Timestamp = 5 * TimeSpan.TicksPerSecond;
        fixture.Pool.BeforePublication = () => throw new InvalidOperationException("publication failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Pool.Retarget(fixture.Connection, true));
        await fixture.Pool.AssignExplicit(fixture.Connection, false).WaitAsync(Timeout);
        Assert.Equal(55, fixture.Connection.Context.Difficulty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecursiveAssignment_FailsImmediatelyAcrossAwaitAndReleasesTheGate(bool childTask)
    {
        await using var fixture = new Fixture();
        await fixture.Pool.AssignOperation(fixture.Connection, async () =>
        {
            await Task.Yield();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => childTask
                ? Task.Run(() => fixture.Pool.AssignExplicit(fixture.Connection, false))
                : fixture.Pool.AssignExplicit(fixture.Connection, false));
            Assert.Contains("re-enter", failure.Message);
            Assert.Equal(0, fixture.Connection.Context.AssignmentGate.CurrentCount);
            Assert.Equal(10, fixture.Connection.Context.Difficulty);
        }).WaitAsync(Timeout);
        await fixture.Pool.AssignExplicit(fixture.Connection, false).WaitAsync(Timeout);
        Assert.Equal(55, fixture.Connection.Context.Difficulty);
        Assert.Equal(1, fixture.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignmentOwnership_RejectsOtherWorkersBeforeWaiting(bool childTask)
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        await first.Pool.AssignOperation(first.Connection, async () =>
        {
            await Task.Yield();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => childTask
                ? Task.Run(() => second.Pool.AssignExplicit(second.Connection, false))
                : second.Pool.AssignExplicit(second.Connection, false));
            Assert.Contains("cannot nest across workers", error.Message);
            Assert.Equal(1, second.Connection.Context.AssignmentGate.CurrentCount);
            Assert.Equal(10, second.Connection.Context.Difficulty);
        }).WaitAsync(Timeout);
        Assert.Equal(1, first.Connection.Context.AssignmentGate.CurrentCount);
        Assert.Equal(1, second.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Fact]
    public async Task IndependentOppositeOrderAssignments_BothFailWithoutDeadlockAndReleaseTheirGates()
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        var bothOwned = Signal();
        var owners = 0;
        Task Attempt(Fixture owner, Fixture target) => Task.Run(() => owner.Pool.AssignOperation(owner.Connection, async () =>
        {
            if(Interlocked.Increment(ref owners) == 2)
                bothOwned.SetResult();
            await bothOwned.Task.WaitAsync(Timeout);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => target.Pool.AssignExplicit(target.Connection, false));
            Assert.Contains("cannot nest across workers", error.Message);
        }));
        await Task.WhenAll(Attempt(first, second), Attempt(second, first)).WaitAsync(Timeout);
        await Task.WhenAll(first.Pool.AssignExplicit(first.Connection, false), second.Pool.AssignExplicit(second.Connection, false))
            .WaitAsync(Timeout);
        Assert.Equal(55, first.Connection.Context.Difficulty);
        Assert.Equal(55, second.Connection.Context.Difficulty);
        Assert.Equal(1, first.Connection.Context.AssignmentGate.CurrentCount);
        Assert.Equal(1, second.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Fact]
    public async Task DetachedBackgroundAssignment_WithSuppressedFlowWaitsThenAcquiresAfterOwnerRelease()
    {
        await using var fixture = new Fixture();
        var waiting = Signal();
        fixture.Pool.AssignmentWaiting = () => waiting.TrySetResult();
        Task child = null;
        await fixture.Pool.AssignOperation(fixture.Connection, async () =>
        {
            // Suppression applies only to synchronous task scheduling, never await.
            using(ExecutionContext.SuppressFlow())
                child = Task.Run(() => fixture.Pool.AssignExplicit(fixture.Connection, false));
            await waiting.Task.WaitAsync(Timeout);
            Assert.False(child.IsCompleted);
            Assert.Equal(10, fixture.Connection.Context.Difficulty);
        }).WaitAsync(Timeout);
        await child.WaitAsync(Timeout);
        Assert.Equal(55, fixture.Connection.Context.Difficulty);
        Assert.Equal(1, fixture.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Fact]
    public async Task ReleasedParentOwnership_DoesNotPermitChildToNestAnotherWorker()
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        var releaseChild = Signal();
        Task child = null;
        await first.Pool.AssignOperation(first.Connection, () =>
        {
            child = Task.Run(async () =>
            {
                await releaseChild.Task;
                await second.Pool.AssignOperation(second.Connection, async () =>
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => first.Pool.AssignExplicit(first.Connection, false));
                    Assert.Contains("cannot nest across workers", error.Message);
                });
            });
            return Task.CompletedTask;
        }).WaitAsync(Timeout);
        releaseChild.SetResult();
        await child.WaitAsync(Timeout);
        Assert.Equal(10, first.Connection.Context.Difficulty);
        Assert.Equal(1, first.Connection.Context.AssignmentGate.CurrentCount);
        Assert.Equal(1, second.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Fact]
    public async Task ChildOperation_AfterOwnerReleasedCanAcquireWithoutStaleAmbientOwnership()
    {
        await using var fixture = new Fixture();
        var releaseChild = Signal();
        Task child = null;
        await fixture.Pool.AssignOperation(fixture.Connection, () =>
        {
            child = Task.Run(async () =>
            {
                await releaseChild.Task;
                await fixture.Pool.AssignExplicit(fixture.Connection, false);
            });
            return Task.CompletedTask;
        });
        releaseChild.SetResult();
        await child.WaitAsync(Timeout);
        Assert.Equal(55, fixture.Connection.Context.Difficulty);
        Assert.Equal(1, fixture.Connection.Context.AssignmentGate.CurrentCount);
    }

    [Fact]
    public async Task ContendedIdleSweep_SkipsWithoutTouchingTimingOrQueueingBehindTheOwner()
    {
        await using var fixture = new Fixture();
        var context = fixture.Connection.Context;
        var timestamp = context.VarDiff.LastShareTimestamp;
        fixture.Time.Timestamp = 30 * TimeSpan.TicksPerSecond;
        await context.AssignmentGate.WaitAsync();
        try
        {
            await fixture.Pool.Retarget(fixture.Connection, true).WaitAsync(Timeout);
            Assert.Equal(timestamp, context.VarDiff.LastShareTimestamp);
            Assert.Null(context.VarDiff.LastUpdate);
            Assert.False(context.HasPendingDifficulty);
            Assert.Empty(fixture.Pool.Notifications);
            Assert.Equal(0, context.AssignmentGate.CurrentCount);
        }
        finally { context.AssignmentGate.Release(); }
        await fixture.Pool.Retarget(fixture.Connection, true).WaitAsync(Timeout);
        Assert.Single(fixture.Pool.Notifications);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MinerSweep_OnlySuppressesCancellationOfItsOwningToken(bool shutdown)
    {
        await using var fixture = new Fixture();
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Error, LogLevel.Fatal, target);
        logs.Configuration = logging;
        fixture.Pool.SetLogger(logs.GetLogger("shutdown-sweep"));
        fixture.Connection.Context.IsAuthorized = true;
        using var cancel = new CancellationTokenSource();
        var sweep = fixture.Pool.Sweep(async () =>
        {
            await Task.Yield();
            if(shutdown)
                cancel.Cancel();
            throw new OperationCanceledException("sweep canceled", cancel.Token);
        }, cancel.Token);
        if(shutdown)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
        else
            await sweep;
        Assert.Equal(shutdown ? 0 : 1, target.Logs.Count);
        Assert.Equal(!shutdown, fixture.Connection.IsDisconnectRequested);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task IdleSweep_DoesNotLogHostCancellationEvenWithoutVarDiff(bool shutdown, bool disabled)
    {
        await using var fixture = new Fixture();
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Error, LogLevel.Fatal, target);
        logs.Configuration = logging;
        fixture.Pool.SetLogger(logs.GetLogger("idle-shutdown"));
        using var cancel = new CancellationTokenSource();
        if(disabled)
            fixture.Connection.Context.VarDiff = null;
        if(shutdown)
            cancel.Cancel();
        else
        {
            fixture.Time.Timestamp = 5 * TimeSpan.TicksPerSecond;
            fixture.Pool.BeforePublication = () => throw new OperationCanceledException("independent timeout");
        }
        await fixture.Pool.UpdateIdleVarDiffAsync(fixture.Connection, cancel.Token);
        Assert.Equal(shutdown ? 0 : 1, target.Logs.Count);
        Assert.False(fixture.Connection.IsDisconnectRequested);
    }

    private sealed class BlockingTimeProvider : TimeProvider
    {
        internal long Timestamp;
        internal Action OnSample;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            OnSample?.Invoke();
            return Timestamp;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IContainer container;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly CancellationTokenSource stop = new();
        private readonly TcpClient client = new(AddressFamily.InterNetwork);
        private readonly Task dispatch;
        internal readonly BlockingTimeProvider Time = new();
        internal TestPool Pool { get; }
        internal StratumConnection Connection { get; }

        internal Fixture(string family = null, bool deferred = false)
        {
            var builder = new ContainerBuilder();
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
            container = builder.Build();
            var clock = new MockMasterClock { CurrentTime = DateTime.UnixEpoch.AddDays(20000) };
            var streams = new RecyclableMemoryStreamManager();
            Pool = new TestPool(container, clock, streams, new NicehashService(Substitute.For<IHttpClientFactory>(), cache),
                family, deferred) { VarDiffTimeProvider = Time };
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint) listener.LocalEndpoint;
            var options = new VarDiffConfig { MinDiff = 1, TargetTime = 10, RetargetTime = 5, VariancePercent = 1 };
            var port = new PoolEndpoint { Difficulty = 10, VarDiff = options };
            Pool.Configure(new PoolConfig { Id = "vardiff-race", EnableInternalStratum = true,
                Ports = new Dictionary<int, PoolEndpoint> { [endpoint.Port] = port },
                Template = new BitcoinTemplate { Symbol = "BTC" } }, new ClusterConfig());
            client.Connect(endpoint);
            var socket = listener.AcceptSocket();
            Connection = new StratumConnection(new NullLogger(LogManager.LogFactory), streams, clock, "race", false);
            Pool.Connect(Connection, endpoint);
            Pool.AddConnection(Connection);
            dispatch = Connection.DispatchAsync(socket, stop.Token, new StratumEndpoint(endpoint, port),
                (IPEndPoint) socket.RemoteEndPoint, null, (_, _, _) => Task.CompletedTask, _ => { }, (_, _) => { });
            Assert.Null(VarDiffManager.Update(Connection.Context, options, clock));
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            client.Dispose();
            await dispatch.WaitAsync(Timeout);
            stop.Dispose();
            cache.Dispose();
            container.Dispose();
        }
    }

    private sealed class TestPool : PoolBase
    {
        private readonly string family;
        private readonly bool deferred;
        internal Func<Task> BeforePublication;
        internal Action AssignmentWaiting;
        internal List<double> Notifications { get; } = new();

        internal TestPool(IComponentContext ctx, IMasterClock clock, RecyclableMemoryStreamManager streams,
            NicehashService nicehash, string family, bool deferred) : base(ctx, new JsonSerializerSettings(),
                Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(), Substitute.For<IMapper>(),
                clock, Substitute.For<IMessageBus>(), streams, nicehash)
        {
            this.family = family;
            this.deferred = deferred;
        }

        internal void SetLogger(ILogger value) => logger = value;
        internal void Connect(StratumConnection connection, IPEndPoint endpoint) => OnConnect(connection, endpoint);
        internal void AddConnection(StratumConnection connection) => RegisterConnection(connection);
        internal Task Sweep(Func<Task> operation, CancellationToken ct) => ForEachMinerAsync((_, _) => operation(), ct);
        internal Task Retarget(StratumConnection connection, bool idle, CancellationToken ct = default) =>
            UpdateVarDiffAsync(connection, idle, ct);
        internal Task AssignOperation(StratumConnection connection, Func<Task> operation) =>
            RunAssignmentAsync(connection, operation);
        internal Task AssignExplicit(StratumConnection connection, bool replace) => RunAssignmentAsync(connection, () =>
        {
            connection.Context.VarDiff = replace ? new VarDiffContext(new ManualTimeProvider())
                { Config = connection.Context.VarDiff.Config } : null;
            connection.Context.SetDifficulty(55);
            Notifications.Add(55);
            return Task.CompletedTask;
        });

        internal override async ValueTask<WorkerAssignmentLease> EnterAssignmentAsync(StratumConnection connection, CancellationToken ct, bool skipIfBusy = false)
        {
            var wait = base.EnterAssignmentAsync(connection, ct, skipIfBusy);
            if(!wait.IsCompleted)
                AssignmentWaiting?.Invoke();
            return await wait;
        }

        protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
        {
            if(BeforePublication != null)
                await BeforePublication();
            await base.OnVarDiffUpdateAsync(connection, newDiff, ct);
            if(!deferred && connection.Context.ApplyPendingDifficulty())
                Notifications.Add(connection.Context.Difficulty);
        }

        protected override WorkerContextBase CreateWorkerContext() => family == null ? new WorkerContextBase() :
            (WorkerContextBase) Activator.CreateInstance(typeof(WorkerContextBase).Assembly.GetType(family));
        protected override Task SetupJobManager(CancellationToken ct) => Task.CompletedTask;
        protected override Task OnRequestAsync(StratumConnection connection, Timestamped<JsonRpcRequest> request,
            CancellationToken ct) => Task.CompletedTask;
        public override double HashrateFromShares(double shares, double interval) => 0;
    }
}
