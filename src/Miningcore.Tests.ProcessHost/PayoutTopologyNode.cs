using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miningcore;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Time;
using Newtonsoft.Json;
using Prometheus;

// Disposable integration process. Only pool discovery and telemetry are supplied
// by this fixture: registration, ownership, classification, allocation and wallet
// RPC execution use the production services and real PostgreSQL/Bitcoin wallets.
internal static class PayoutTopologyNode
{
    public static async Task<int> RunAsync()
    {
        var config = JsonConvert.DeserializeObject<ClusterConfig>(
            Environment.GetEnvironmentVariable("MININGCORE_TEST_CLUSTER"));
        var coreBuilder = new ContainerBuilder();
        coreBuilder.RegisterModule<Miningcore.AutofacModule>();
        await using var core = coreBuilder.Build();
        var templates = CoinTemplateLoader.Load(core, new[] { Path.Combine(AppContext.BaseDirectory, "coins.json") });
        Miningcore.Program.AssignPoolTemplates(config.Pools, templates);
        var database = new PgConnectionFactory(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        var mapper = AutoMapperFactory.CreateMapper();
        var blocks = new BlockRepository(mapper);
        var shares = new ShareRepository(mapper);
        var balances = new BalanceRepository(mapper);
        var bus = new MessageBus();
        var probe = new Probe();
        var payouts = new ContainerBuilder();
        payouts.Register(_ => new ObservedHandler(core, database, probe)).As<IPayoutHandler>()
            .WithMetadata(nameof(CoinFamilyAttribute.SupportedFamilies), new[] { CoinFamily.Bitcoin });
        payouts.RegisterInstance(new SOLOPaymentScheme(shares, balances)).Keyed<IPayoutScheme>(PayoutScheme.SOLO);
        payouts.RegisterInstance(new PPSPaymentScheme(shares)).Keyed<IPayoutScheme>(PayoutScheme.PPS);
        await using var payoutScope = payouts.Build();
        await using var lease = new PostgresPayoutManagerLease(database);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILifetimeScope>(payoutScope);
        builder.Services.AddSingleton<IConnectionFactory>(database);
        builder.Services.AddSingleton<IBlockRepository>(blocks);
        builder.Services.AddSingleton<IShareRepository>(shares);
        builder.Services.AddSingleton<IBalanceRepository>(balances);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IMessageBus>(bus);
        builder.Services.AddSingleton<IPayoutManagerLease>(lease);
        builder.Services.AddSingleton<IProcessStatus>(new ProcessStatus());
        Miningcore.Program.ConfigurePaymentProcessingHostedService(builder.Services, config);
        var registry = Metrics.NewCustomRegistry();
        builder.Services.AddSingleton<IHostedService>(new PayoutPauseMonitor(config,
            Metrics.WithCustomRegistry(registry), _ => Interlocked.Increment(ref probe.Heartbeats)));
        using var host = builder.Build();
        foreach(var manager in host.Services.GetServices<IHostedService>().OfType<PayoutManager>())
            foreach(var pool in config.Pools)
                manager.AttachPool(new ConfiguredPool(pool));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try { await host.StartAsync(deadline.Token); }
        catch(PoolStartupException)
        {
            Console.WriteLine("LAB blocked");
            return 78;
        }
        Console.WriteLine("LAB ready");
        try
        {
            while(await Console.In.ReadLineAsync(deadline.Token) is { } command && command != "stop")
            {
                if(command != "state") throw new InvalidOperationException("Unknown lab command");
                await using var metrics = new MemoryStream();
                await registry.CollectAndExportAsTextAsync(metrics);
                Console.WriteLine("LAB " + JsonConvert.SerializeObject(new
                {
                    processor = Miningcore.Program.ShouldRunPaymentProcessor(config),
                    cycles = Volatile.Read(ref probe.Cycles),
                    heartbeats = Volatile.Read(ref probe.Heartbeats),
                    metrics = System.Text.Encoding.UTF8.GetString(metrics.ToArray()),
                }));
            }
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(stop.Token);
        }
        return 0;
    }

    private sealed class Probe { public int Cycles; public int Heartbeats; }

    private sealed class ObservedHandler(IComponentContext core, PgConnectionFactory database, Probe probe) :
        BitcoinPayoutHandler(core, database, AutoMapperFactory.CreateMapper(),
            new ShareRepository(AutoMapperFactory.CreateMapper()), new BlockRepository(AutoMapperFactory.CreateMapper()),
            new BalanceRepository(AutoMapperFactory.CreateMapper()), new PaymentRepository(AutoMapperFactory.CreateMapper()),
            new StandardClock(), new MessageBus(), new ActiveBlockGracePeriodTracker())
    {
        public override async Task ConfigureAsync(ClusterConfig cluster, PoolConfig pool, CancellationToken ct)
        {
            await base.ConfigureAsync(cluster, pool, ct);
            Interlocked.Increment(ref probe.Cycles);
        }
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
