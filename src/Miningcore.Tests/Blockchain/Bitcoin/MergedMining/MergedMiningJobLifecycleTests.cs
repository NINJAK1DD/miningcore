using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public partial class MergedMiningManagerReorgTests
{
    [Fact]
    public async Task ForcedParentFailureBeforeFirstJob_SuppressesPublication()
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        manager.ParentUnavailable = true;

        var result = await manager.ForceUpdate(CancellationToken.None);

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task ForcedEmptyParentResponseBeforeFirstJob_SuppressesPublication()
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        manager.ParentEmptyResponse = true;

        var result = await manager.ForceUpdate(CancellationToken.None);

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task ForcedAuxiliaryFailureBeforeFirstJob_SuppressesPublicationAndReportsUnavailable()
    {
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.RpcError(-1, "auxiliary unavailable"));
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out var messageBus);
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        manager.Enqueue(CreateParentTemplate());
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var subscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        var result = await manager.ForceUpdate(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Null(manager.Current);
        var unavailable = Assert.Single(stateEvents);
        Assert.False(unavailable.Available);
        Assert.False(unavailable.Degraded);
        Assert.False(unavailable.FallbackStarted);
    }

    [Fact]
    public async Task ForcedRefreshExceptionBeforeFirstJob_SuppressesPublication()
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        manager.ParentRefreshException = new InvalidOperationException(
            "parent refresh failed");

        var result = await manager.ForceUpdate(CancellationToken.None);

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Null(manager.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingVerifiedJob_RebroadcastsAcrossParentRefreshFailures(
        bool throws)
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), CreateAuxiliaryTemplate());
        var verified = manager.Current;
        if(throws)
        {
            manager.ParentRefreshException = new InvalidOperationException(
                "parent refresh failed");
        }
        else
            manager.ParentUnavailable = true;

        var result = await manager.ForceUpdate(CancellationToken.None);

        Assert.False(result.IsNew);
        Assert.True(result.Force);
        Assert.Same(verified, manager.Current);
    }

    [Fact]
    public async Task ExistingVerifiedJob_RebroadcastsAcrossEmptyParentResponse()
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), CreateAuxiliaryTemplate());
        var verified = manager.Current;
        manager.ParentEmptyResponse = true;

        var result = await manager.ForceUpdate(CancellationToken.None);

        Assert.False(result.IsNew);
        Assert.True(result.Force);
        Assert.Same(verified, manager.Current);
    }

    [Fact]
    public async Task ShutdownCancellation_DoesNotRequestARebroadcast()
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), CreateAuxiliaryTemplate());
        var verified = manager.Current;
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        manager.ParentRefreshException = new OperationCanceledException(
            stop.Token);

        var result = await manager.ForceUpdate(stop.Token);

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Same(verified, manager.Current);
    }

    [Fact]
    public async Task SharedPipeline_DropsUnsafeNullUpdatesAndLogsWaitOnce()
    {
        var previousLogging = LogManager.Configuration;
        using var target = new MemoryTarget { Layout = "${level}|${message}" };
        var logging = new LoggingConfiguration();
        logging.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
        LogManager.Configuration = logging;

        try
        {
            using var dependencies = BuildLifecycleDependencies();
            var manager = CreateLifecycleManager(dependencies, out var messageBus);
            var (parent, _, cluster) = CreateConfig();
            manager.ReturnUnsafeForcedResult = true;
            manager.Configure(parent, cluster);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            manager.InitializeJobUpdates(stop.Token);
            var publications = 0;
            using var subscription = manager.Jobs.Subscribe(_ =>
                Interlocked.Increment(ref publications));

            var now = DateTime.UtcNow;
            messageBus.SendMessage(new BtStreamMessage("ltc-templates",
                "first", now, now));
            await manager.ForcedRefresh.Task.WaitAsync(stop.Token);
            messageBus.SendMessage(new BtStreamMessage("ltc-templates",
                "second", now, now));
            await manager.SecondForcedRefresh.Task.WaitAsync(stop.Token);

            // The completion sources run inside UpdateJob. Give the synchronous
            // Rx continuation time to apply the publication boundary.
            await Task.Delay(50, stop.Token);
            LogManager.Flush();

            Assert.Equal(0, publications);
            Assert.Null(manager.Current);
            Assert.Single(target.Logs.Where(x => x.Contains(
                "Job publication suppressed because no verified job is available yet",
                StringComparison.Ordinal)));
        }
        finally
        {
            LogManager.Configuration = previousLogging;
        }
    }

    [Fact]
    public async Task ExistingVerifiedJob_RebroadcastsWithCachedAuxiliaryFallback()
    {
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.RpcError(-1, "auxiliary unavailable"));
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out var messageBus);
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        var cachedAuxiliary = CreateAuxiliaryTemplate();
        manager.Seed(CreateParentTemplate(), cachedAuxiliary);
        var verified = manager.Current;
        manager.Enqueue(CreateParentTemplate());
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var subscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        var result = await manager.ForceUpdate(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsNew);
        Assert.True(result.Force);
        Assert.NotSame(verified, manager.Current);
        Assert.Same(cachedAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        var degraded = Assert.Single(stateEvents);
        Assert.True(degraded.Available);
        Assert.True(degraded.Degraded);
        Assert.True(degraded.FallbackStarted);
    }

    [Fact]
    public async Task NonMergedForcedFailure_UsesBaseNullJobGuard()
    {
        using var dependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(dependencies, out _);
        var (parent, _, cluster) = CreateConfig();
        parent.Extra = new Dictionary<string, object>();
        manager.Configure(parent, cluster);
        manager.ParentUnavailable = true;

        var result = await manager.ForceUpdate(CancellationToken.None);

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Null(manager.Current);
        Assert.Null(manager.StartupAuxiliaryTemplate);
    }

    [Fact]
    public async Task PoolStartup_WaitsThroughForcedParentFailureThenActivatesOnRecovery()
    {
        ModuleInitializer.Initialize();
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.Success(CreateAuxiliaryTemplate()));
        using var managerDependencies = BuildLifecycleDependencies();
        var manager = CreateLifecycleManager(managerDependencies,
            out var messageBus);
        manager.ParentUnavailable = true;
        manager.LifecycleStartupAuxiliary = CreateAuxiliaryTemplate();
        manager.Enqueue(CreateParentTemplate());

        var clock = new StandardClock();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var nicehash = new NicehashService(httpClientFactory, cache);
        using var scope = ModuleInitializer.Container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IConnectionFactory>());
            builder.RegisterInstance(Substitute.For<IStatsRepository>());
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
            builder.RegisterInstance(httpClientFactory);
            builder.RegisterInstance(cache).As<IMemoryCache>();
            builder.RegisterInstance(nicehash);
            builder.RegisterInstance(clock).As<IMasterClock>();
            builder.RegisterInstance(messageBus).As<IMessageBus>();
            builder.RegisterInstance(manager).As<BitcoinJobManager>();
        });
        var pool = ResolveMergedMiningPool(scope);

        using var listenerSocket = StratumServer.CreateBoundSocket(
            new IPEndPoint(IPAddress.Loopback, 0));
        var listenerEndPoint = (IPEndPoint) listenerSocket.LocalEndPoint;
        var poolEndpoint = new PoolEndpoint
        {
            Difficulty = 1,
            ListenAddress = IPAddress.Loopback.ToString(),
        };
        var reservation = new StratumListenerReservation("ltc-solo",
            new StratumEndpoint(listenerEndPoint, poolEndpoint), listenerSocket);
        var reservationSession = new StratumListenerReservationSession(
            new Dictionary<string, StratumListenerReservation[]>
            {
                ["ltc-solo"] = new[] { reservation },
            });
        var (parent, _, cluster) = CreateConfig(server.Port);
        parent.EnableInternalStratum = true;
        parent.BlockRefreshInterval = 50;
        parent.JobRebroadcastTimeout = 1;
        parent.Ports = new Dictionary<int, PoolEndpoint>
        {
            [listenerEndPoint.Port] = poolEndpoint,
        };
        parent.Extra = new Dictionary<string, object>
        {
            ["mergedMining"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["auxPoolId"] = "doge-solo",
                ["auxiliaryTemplatePollTimeoutMs"] = 500,
            },
        };
        cluster.Logging = new ClusterLoggingConfig();
        pool.Configure(parent, cluster);
        pool.AttachStratumListenerReservations(reservationSession);
        var online = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var statusSubscription = messageBus.Listen<PoolStatusNotification>()
            .Where(x => ReferenceEquals(x.Pool, pool) &&
                x.Status == PoolStatus.Online)
            .Subscribe(_ => online.TrySetResult());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = pool.RunAsync(stop.Token);

        try
        {
            await manager.ForcedRefresh.Task.WaitAsync(stop.Token);
            // ForcedRefresh completes inside UpdateJob, before the Rx pipeline
            // consumes its tuple. The parent remains unavailable throughout this
            // short drain, so no legitimate publication can race the assertions.
            await Task.Delay(100, stop.Token);

            Assert.Equal(0, manager.Publications);
            Assert.Null(manager.Current);
            Assert.False(run.IsCompleted);
            Assert.False(reservation.IsActivated);
            Assert.False(online.Task.IsCompleted);

            manager.ParentUnavailable = false;
            await manager.FirstPublication.Task.WaitAsync(stop.Token);
            await online.Task.WaitAsync(stop.Token);

            Assert.Equal(1, manager.Publications);
            Assert.NotNull(manager.Current);
            Assert.True(reservation.IsActivated);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested)
            {
            }
            reservationSession.Dispose();
        }
    }

    private static IContainer BuildLifecycleDependencies()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        return builder.Build();
    }

    private static TestManager CreateLifecycleManager(IComponentContext dependencies,
        out MessageBus messageBus)
    {
        messageBus = new MessageBus();
        return new TestManager(dependencies, new StandardClock(), messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
    }

    private static MergedMiningBitcoinPool ResolveMergedMiningPool(
        ILifetimeScope scope) => Assert.IsType<MergedMiningBitcoinPool>(
        Assert.Single(scope
            .Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinFamilyAttribute>>>>()
            .Where(x => x.Value.Metadata.SupportedFamilies
                .Contains(CoinFamily.Bitcoin))).Value.Value);

    private sealed partial class TestManager
    {
        internal readonly TaskCompletionSource ForcedRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondForcedRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstPublication = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal AuxBlockTemplate LifecycleStartupAuxiliary { get; set; }
        internal bool ReturnUnsafeForcedResult { get; set; }
        internal int Publications;
        private int forcedRefreshCount;

        protected override Task<bool> AreDaemonsHealthyAsync(
            CancellationToken ct) => Task.FromResult(true);

        protected override Task<bool> AreDaemonsConnectedAsync(
            CancellationToken ct) => Task.FromResult(true);

        protected override Task EnsureDaemonsSynchedAsync(CancellationToken ct)
        {
            if(LifecycleStartupAuxiliary != null)
                CacheStartupAuxiliaryTemplate(LifecycleStartupAuxiliary);

            return Task.CompletedTask;
        }

        protected override Task PostStartInitAsync(CancellationToken ct)
        {
            SetupJobUpdates(ct);
            return Task.CompletedTask;
        }

        protected override async Task<(bool IsNew, bool Force)> UpdateJob(
            CancellationToken ct, bool forceUpdate, string via = null,
            string json = null)
        {
            var result = forceUpdate && ReturnUnsafeForcedResult
                ? (IsNew: false, Force: true)
                : await base.UpdateJob(ct, forceUpdate, via, json);
            if(forceUpdate)
            {
                ForcedRefresh.TrySetResult();
                if(Interlocked.Increment(ref forcedRefreshCount) == 2)
                    SecondForcedRefresh.TrySetResult();
            }
            return result;
        }

        protected override object GetJobParamsForStratum(bool isNew)
        {
            Interlocked.Increment(ref Publications);
            var result = base.GetJobParamsForStratum(isNew);
            FirstPublication.TrySetResult();
            return result;
        }
    }
}
