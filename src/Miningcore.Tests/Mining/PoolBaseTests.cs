using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Disposables;
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
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class PoolBaseTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_WithoutInternalStratum_RemainsOnlineUntilCancellation()
    {
        var messageBus = new MessageBus();
        var online = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var statusSubscription = messageBus.Listen<PoolStatusNotification>()
            .Subscribe(x =>
            {
                if(x.Status == PoolStatus.Online)
                    online.TrySetResult(true);
            });
        using var container = BuildContainer();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var pool = new TestPool(container, messageBus,
            new NicehashService(httpClientFactory, cache));
        var config = new PoolConfig
        {
            Id = "relay-receiver",
            EnableInternalStratum = false,
            Ports = new Dictionary<int, PoolEndpoint>(),
            Template = new BitcoinTemplate { Symbol = "LTC" },
        };
        pool.Configure(config, new ClusterConfig
        {
            ShareRelays = new[] { new ShareRelayEndpointConfig { Url = "tcp://127.0.0.1:5555" } },
        });
        using var cts = new CancellationTokenSource();

        var runTask = pool.RunAsync(cts.Token);
        await pool.SetupCompleted.Task.WaitAsync(TestTimeout);
        await online.Task.WaitAsync(TestTimeout);

        Assert.False(runTask.IsCompleted);
        Assert.False(pool.JobSubscriptionDisposed);

        cts.Cancel();
        await runTask.WaitAsync(TestTimeout);

        Assert.True(pool.JobSubscriptionDisposed);
    }

    [Fact]
    public async Task WaitForShutdownAsync_CompletesPromptlyAfterCancellation()
    {
        using var cts = new CancellationTokenSource();
        var waitTask = PoolBase.WaitForShutdownAsync(cts.Token);

        Assert.False(waitTask.IsCompleted);

        cts.Cancel();
        await waitTask.WaitAsync(TestTimeout);
    }

    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(Substitute.For<IBlockRepository>());
        builder.RegisterInstance(Substitute.For<IShareRepository>());
        return builder.Build();
    }

    private sealed class TestPool : PoolBase
    {
        public TestPool(IComponentContext ctx, IMessageBus messageBus,
            NicehashService nicehashService) : base(ctx,
            new JsonSerializerSettings(),
            Substitute.For<IConnectionFactory>(),
            Substitute.For<IStatsRepository>(),
            Substitute.For<IMapper>(),
            Substitute.For<IMasterClock>(),
            messageBus,
            new RecyclableMemoryStreamManager(),
            nicehashService)
        {
        }

        public TaskCompletionSource<bool> SetupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool JobSubscriptionDisposed { get; private set; }

        protected override Task SetupJobManager(CancellationToken ct)
        {
            disposables.Add(Disposable.Create(() => JobSubscriptionDisposed = true));
            SetupCompleted.TrySetResult(true);
            return Task.CompletedTask;
        }

        protected override Task InitStatsAsync(CancellationToken ct)
        {
            blockchainStats = new BlockchainStats
            {
                NetworkType = "RegTest",
                RewardType = "POW",
            };
            return Task.CompletedTask;
        }

        protected override WorkerContextBase CreateWorkerContext() => null;

        protected override Task OnRequestAsync(StratumConnection connection,
            Timestamped<JsonRpcRequest> request, CancellationToken ct) => Task.CompletedTask;

        public override double HashrateFromShares(double shares, double interval) => 0;
    }
}
