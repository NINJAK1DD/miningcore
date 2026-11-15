using System.Collections.Concurrent;
using System.Reactive.Linq;
using Autofac;
using Microsoft.Extensions.Hosting;
using Miningcore;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Pearl;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Time;
using Newtonsoft.Json;

// Fresh-process replay of a native-verified Pearl candidate retained by the parent
// test. No proof, accounting identity or receipt is manufactured by this child.
internal static class PearlRelayReplay
{
    public static async Task<int> RunAsync()
    {
        var config = JsonConvert.DeserializeObject<ClusterConfig>(Environment.GetEnvironmentVariable("MININGCORE_TEST_CLUSTER"));
        var builder = new ContainerBuilder();
        builder.RegisterModule<Miningcore.AutofacModule>();
        await using var core = builder.Build();
        Miningcore.Program.AssignPoolTemplates(config.Pools,
            CoinTemplateLoader.Load(core, new[] { Path.Combine(AppContext.BaseDirectory, "coins.json") }));
        var pool = config.Pools.Single();
        var database = new PgConnectionFactory(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        var mapper = AutoMapperFactory.CreateMapper();
        var repository = new ShareRepository(mapper);
        var store = new PearlCandidateStore(database, repository, pool);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var retained = await store.NextAsync(pool.Id, deadline.Token);
        var replay = JsonConvert.DeserializeObject<Share>(retained.Share);
        using var host = Host.CreateApplicationBuilder().Build();
        var status = new ProcessStatus();
        using var coordinator = new MiningFailStopCoordinator(status,
            (IHostApplicationLifetime) host.Services.GetService(typeof(IHostApplicationLifetime)));
        var receiving = new ClusterConfig
        {
            Pools = config.Pools, PaymentProcessing = config.PaymentProcessing,
            ShareRecoveryFile = config.ShareRecoveryFile,
            ShareRecoveryStateDirectory = config.ShareRecoveryStateDirectory,
            ShareRelays = new[] { new ShareRelayEndpointConfig
            { Url = config.ShareRelay.PublishUrl, SharedEncryptionKey = config.ShareRelay.SharedEncryptionKey } },
        };
        var bus = new MessageBus(coordinator);
        var failure = new ShareRecoveryFailureHandler(coordinator,
            new Lazy<ICriticalNotificationSender>(() => new UnexpectedNotification()),
            new ShareRecoveryFatalState(receiving, status, receiving.ShareRecoveryStateDirectory));
        using var ownership = new ShareRecoveryPathOwnership(receiving);
        ownership.Acquire();
        using var recorder = new ShareRecorder(database, mapper, new JsonSerializerSettings(), repository,
            new BlockRepository(mapper), receiving, bus, failure, coordinator, ownership);
        using var receiver = new ShareReceiver(receiving, new StandardClock(), bus,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        var received = new ConcurrentQueue<Share>();
        using var observed = bus.Listen<Share>().Subscribe(received.Enqueue);
        // The publisher must have a separate bus to avoid re-publishing received frames.
        var outgoing = new MessageBus(coordinator);
        using var publisher = new ShareRelay(config, outgoing, coordinator);
        await recorder.StartAsync(deadline.Token);
        await receiver.StartAsync(deadline.Token);
        bus.SendMessage(new PoolStatusNotification { Pool = new ConfiguredPool(pool), Status = PoolStatus.Online });
        await publisher.StartAsync(deadline.Token);
        try
        {
            while(received.Count < 3)
            {
                outgoing.SendMessage(replay);
                await Task.Delay(100, deadline.Token);
            }
            await publisher.StopAsync(deadline.Token);
            await receiver.StopAsync(deadline.Token);
            await Task.WhenAll(received.Select(x => x.PersistenceAdmission)).WaitAsync(deadline.Token);
            await recorder.StopAsync(deadline.Token);
            var id = Guid.Parse(replay.AccountingId);
            var hashes = received.Select(value => ShareAccounting.ComputePayloadHash(id,
                new[] { ShareAccounting.ToPersistenceShare(value) }, new[] { ShareAccounting.CreatePpsCredit(pool, value) })).Distinct().ToArray();
            var result = JsonConvert.SerializeObject(new
            {
                processId = Environment.ProcessId, deliveries = received.Count, hashes,
                sources = received.Select(x => x.Source).Distinct().ToArray(),
                failStop = coordinator.IsFailStopRequested,
                journal = File.Exists(receiving.ShareRecoveryFile),
                quarantines = Directory.GetFiles(Path.GetDirectoryName(receiving.ShareRecoveryFile), "*quarantine*", SearchOption.AllDirectories).Length,
            });
            Console.WriteLine("LAB ready");
            while(await Console.In.ReadLineAsync(deadline.Token) is { } command && command != "stop")
            {
                if(command != "state") throw new InvalidOperationException("Unknown lab command");
                Console.WriteLine("LAB " + result);
            }
            return coordinator.IsFailStopRequested ? 78 : 0;
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await publisher.StopAsync(stop.Token);
            await receiver.StopAsync(stop.Token);
            await recorder.StopAsync(stop.Token);
        }
    }

    private sealed class UnexpectedNotification : ICriticalNotificationSender
    {
        public Task SendCriticalAdminNotificationAsync(AdminNotification notification, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected critical accounting notification in replay child");
    }

    private sealed class ConfiguredPool(PoolConfig config) : IMiningPool
    {
        public PoolConfig Config => config;
        public PoolStats PoolStats { get; } = new();
        public BlockchainStats NetworkStats { get; } = new();
        public double ShareMultiplier => 1;
        public void Configure(PoolConfig pc, ClusterConfig cc) => throw new NotSupportedException();
        public double HashrateFromShares(double shares, double interval) => throw new NotSupportedException();
        public Task RunAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
