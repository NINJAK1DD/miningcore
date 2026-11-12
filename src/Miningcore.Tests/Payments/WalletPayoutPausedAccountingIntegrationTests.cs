using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Time;
using Newtonsoft.Json;
using Npgsql;
using NSubstitute;
using Prometheus;
using Xunit;
using Node = Miningcore.Tests.Blockchain.Bitcoin.BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    [WalletControlFact]
    public async Task ExtendedPausedRelay_PersistsPpsAndMonitorsBoundedQueuesAndBacklog()
    {
        // Real wall time: crosses two production five-minute pause heartbeats.
        // This is deliberately a soak test, not an accelerated/mock-clock claim.
        var duration = TimeSpan.FromSeconds(620);
        const int burst = 50;
        await using var db = await Database.Create();
        await using var wallet = await Node.StartAsync(true);
        var cluster = TopologyConfig(wallet, await wallet.GetNewAddressAsync(), true);
        cluster.Pools[0].PaymentProcessing.PayoutScheme = PayoutScheme.PPS;
        await SeedBlocks(db, wallet, cluster.Pools[0], 64);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"tcp://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}";
        listener.Stop();
        cluster.ShareRelays = new[] { new ShareRelayEndpointConfig { Url = url } };
        await using var lab = new AccountingLab(db, cluster);
        await lab.Start();
        using var receiver = new ShareReceiver(cluster, new StandardClock(), lab.Bus,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(15));
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(cluster.Pools[0]);
        pool.NetworkStats.Returns(new BlockchainStats());
        pool.ShareMultiplier.Returns(1d);
        await receiver.StartAsync(CancellationToken.None);
        lab.Bus.SendMessage(new PoolStatusNotification { Pool = pool, Status = PoolStatus.Online });
        await Until(() => Task.FromResult(receiver.AttachedPoolCount == 1), 10);
        var sender = new MessageBus();
        using var relay = new ShareRelay(new ClusterConfig
        {
            ClusterName = "paused-soak-sender", ShareRelay = new ShareRelayConfig { PublishUrl = url },
        }, sender);
        var registry = Metrics.NewCustomRegistry();
        var heartbeats = 0;
        using var monitor = new PayoutPauseMonitor(cluster, Metrics.WithCustomRegistry(registry),
            _ => Interlocked.Increment(ref heartbeats));
        await relay.StartAsync(CancellationToken.None);
        await monitor.StartAsync(CancellationToken.None);
        var storageBefore = await AccountingBytes(db);
        var elapsed = Stopwatch.StartNew();
        var expected = 0;
        decimal owed = 0;
        try
        {
            while(elapsed.Elapsed < duration)
            {
                var batch = Enumerable.Range(0, burst).Select(_ => PpsShare(cluster.Pools[0])).ToArray();
                expected += batch.Length;
                owed += batch.Sum(x => x.PpsCalculatedAmount.Value);
                // PUB/SUB has no replay or delivery acknowledgement. Retry the same
                // immutable IDs until PostgreSQL proves receipt, exercising dedup too.
                await Until(async () =>
                {
                    foreach(var share in batch) sender.SendMessage(share);
                    await Task.Delay(100);
                    return await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits") == expected;
                }, 30);
                Assert.Equal(expected, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
                Assert.Equal(owed, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
                Assert.Equal(64, await PendingCount(db));
                Assert.Equal(0, await PaymentCount(db));
                Assert.InRange(lab.Recorder.PersistenceQueueDepth, 0, lab.Recorder.PersistenceQueueCapacity);
                Assert.InRange(lab.Recorder.PersistenceQueueHighWatermark, 1, lab.Recorder.PersistenceQueueCapacity);
                Assert.Equal(0, lab.Recorder.PersistenceQueueOverflowCount);
                Assert.Equal(0, lab.Recorder.EmergencyJournalQueueOverflowCount);
                Assert.False(lab.Coordinator.IsFailStopRequested);
                Assert.False(Program.ShouldRunPaymentProcessor(cluster));
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            Assert.True(Volatile.Read(ref heartbeats) >= 2);
            await using var metrics = new MemoryStream();
            await registry.CollectAndExportAsTextAsync(metrics);
            Assert.Contains("miningcore_payout_processor_paused 1", Encoding.UTF8.GetString(metrics.ToArray()));
            Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_ownership WHERE owner_id IS NOT NULL"));
            Assert.Equal(0, (await db.State()).OutstandingBatches);
            await AssertWalletReceipts(db, wallet, 0);
            var growth = await AccountingBytes(db) - storageBefore;
            Assert.InRange(growth, 0, 64L * 1024 * 1024);
            output.WriteLine($"Soak seconds={elapsed.Elapsed.TotalSeconds:F1}; unique durable shares/credits={expected}; owed={owed}; pending blocks=64; heartbeats={heartbeats}; queue high-water={lab.Recorder.PersistenceQueueHighWatermark}/{lab.Recorder.PersistenceQueueCapacity}; storage growth={growth} bytes; wallet sends=0.");
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
            await receiver.StopAsync(CancellationToken.None);
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [IsolatedPostgresFact]
    public async Task PausedPps_DatabaseInterruptionRequiresDurabilityBeforeAcknowledgementAndExplicitRecovery()
    {
        await using var server = new IsolatedPostgresServer();
        await server.Initialize();
        var connection = new NpgsqlConnectionStringBuilder
        { Host = "127.0.0.1", Port = server.Port, Username = "miningcore", Database = "postgres", SslMode = SslMode.Disable };
        await using var db = await Database.Create(connection.ConnectionString);
        var config = new ClusterConfig
        {
            PaymentProcessing = new() { Enabled = true, Paused = true },
            Pools = new[] { new PoolConfig { Id = "paused-pps", Enabled = true, Coin = "bitcoin",
                Template = ModuleInitializer.CoinTemplates["bitcoin"], RewardRecipients = Array.Empty<RewardRecipient>(),
                PaymentProcessing = new() { Enabled = true, PayoutScheme = PayoutScheme.PPS } } },
        };
        await using var lab = new AccountingLab(db, config);
        await lab.Start();
        var baseline = PpsShare(config.Pools[0]);
        await lab.Acknowledge(baseline);
        Assert.Equal(1, lab.Acknowledgements);
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));

        // Interrupt a real in-flight PostgreSQL persistence statement, with the
        // positive-response boundary held pending, then continue PPS arrivals.
        await db.Admin.ExecuteAsync("BEGIN; LOCK TABLE pps_share_credits IN ACCESS EXCLUSIVE MODE");
        var arrivals = Enumerable.Range(0, 20).Select(_ => PpsShare(config.Pools[0])).ToArray();
        var responses = arrivals.Select(lab.Acknowledge).ToArray();
        await Until(async () => await db.Admin.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM pg_stat_activity WHERE usename=@app AND wait_event_type='Lock'", new { app = db.AppRole }) > 0, 15);
        Assert.Equal(1, lab.Acknowledgements);
        Assert.All(responses, response => Assert.False(response.IsCompleted));
        await server.StopForOutageAsync();
        try
        {
            await Task.WhenAll(responses).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(21, lab.Acknowledgements);
            Assert.True(File.Exists(config.ShareRecoveryFile));
            Assert.True(File.Exists(lab.Recorder.RecoveryTerminalStateFilename));
            Assert.False(lab.Coordinator.IsFailStopRequested);
            Assert.False(Program.ShouldRunPaymentProcessor(config));
            await lab.Stop();
        }
        finally { await server.ResumeAfterOutageAsync(); await db.ReconnectObservers(); }
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));

        // Recreate the recorder/coordinator, import the authenticated journal,
        // and replay the same evidence. Credits and balances must stay exactly once.
        await using var recovered = new AccountingLab(db, config, reuseDirectory: true);
        var archive = await recovered.Recorder.RecoverSharesAsync(config.ShareRecoveryFile);
        Assert.True(File.Exists(archive));
        Assert.Equal(21, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
        var owed = baseline.PpsCalculatedAmount.Value + arrivals.Sum(x => x.PpsCalculatedAmount.Value);
        Assert.Equal(owed, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        await recovered.Start();
        await Task.WhenAll(arrivals.Select(recovered.Acknowledge)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(21, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
        Assert.Equal(owed, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));

        // Both durability channels fail: never queue a positive response, retain
        // fatal evidence, and keep startup blocked even after PostgreSQL returns.
        recovered.Recorder.RecoveryJournalFlush = _ => Task.FromException(new IOException("lab fsync failure"));
        var acknowledgedBeforeFailure = recovered.Acknowledgements;
        await server.StopForOutageAsync();
        try
        {
            var failed = Enumerable.Range(0, 4).Select(_ => recovered.Acknowledge(PpsShare(config.Pools[0]))).ToArray();
            await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(failed).WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.All(failed, response => Assert.True(response.IsCompleted && !response.IsCompletedSuccessfully));
            await Until(() => Task.FromResult(File.Exists(recovered.Fatal.FatalStateFilename)), 30);
            Assert.True(recovered.Coordinator.IsFailStopRequested);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss, recovered.Status.ExitCode);
            Assert.Equal(acknowledgedBeforeFailure, recovered.Acknowledgements);
            Assert.Throws<PoolStartupException>(() => recovered.Fatal.EnsureStartupAllowed());
            await recovered.Stop();
        }
        finally { await server.ResumeAfterOutageAsync(); await db.ReconnectObservers(); }
        Assert.Equal(21, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
        Assert.Equal(owed, await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
        Assert.Throws<PoolStartupException>(() => recovered.Fatal.EnsureStartupAllowed());
        Assert.Equal(0, await PaymentCount(db));
        Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_ownership WHERE owner_id IS NOT NULL"));
        Assert.True((await db.State()).Paused);
        output.WriteLine("Real PostgreSQL interruption: 20 PPS admissions waited for authenticated durable journal; restart/import/replay preserved 21 credits exactly once. Subsequent database+fsync failure acknowledged zero new shares and persisted a startup-blocking fatal incident.");
    }

    private static Task<long> AccountingBytes(Database db) => db.Admin.ExecuteScalarAsync<long>(
        "SELECT sum(pg_total_relation_size(oid))::bigint FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r'");

    private static Share PpsShare(PoolConfig pool)
    {
        var share = new Share
        {
            PoolId = pool.Id, Miner = pool.Address ?? "lab-miner", Worker = "lab", IpAddress = "127.0.0.1", SessionId = "lab-session",
            Created = DateTime.UtcNow, PreserveCreated = true, AccountingId = Guid.NewGuid().ToString("N"),
            AccountingRole = ShareAccountingRole.Single, Difficulty = 1, ShareDifficulty = 1, ActualDifficulty = 1,
            NetworkDifficulty = 10000, BlockHeight = 100, RewardBasisSatoshis = 5_000_000_000,
        };
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        return share;
    }

    private sealed class AccountingLab : IAsyncDisposable
    {
        public readonly ShareRecorder Recorder;
        public readonly MiningFailStopCoordinator Coordinator;
        public readonly ShareRecoveryFatalState Fatal;
        public readonly ProcessStatus Status = new();
        public readonly MessageBus Bus;
        private readonly IContainer context;
        private readonly AdmissionProbe stratum;
        private readonly string directory;
        private readonly bool ownsDirectory;
        private bool started;
        public int Acknowledgements;
        public AccountingLab(Database db, ClusterConfig config, bool reuseDirectory = false)
        {
            ownsDirectory = !reuseDirectory;
            directory = reuseDirectory ? Path.GetDirectoryName(config.ShareRecoveryFile) :
                Path.Combine(Path.GetTempPath(), "miningcore-paused-accounting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            config.ShareRecoveryFile = Path.Combine(directory, "shares.json");
            config.ShareRecoveryStateDirectory = Path.Combine(directory, "state");
            Coordinator = new MiningFailStopCoordinator(Status, Substitute.For<IHostApplicationLifetime>());
            Bus = new MessageBus(Coordinator);
            Fatal = new ShareRecoveryFatalState(config, Status, config.ShareRecoveryStateDirectory);
            var failure = new ShareRecoveryFailureHandler(Coordinator,
                new Lazy<ICriticalNotificationSender>(() => Substitute.For<ICriticalNotificationSender>()), Fatal);
            var mapper = AutoMapperFactory.CreateMapper();
            Recorder = new ShareRecorder(db.Factory, mapper, new JsonSerializerSettings(),
                new ShareRepository(mapper), new BlockRepository(mapper), config, Bus, failure, Coordinator)
            { PersistenceQueueCapacity = 512, EmergencyJournalQueueCapacity = 128 };
            var builder = new ContainerBuilder();
            builder.RegisterInstance(Coordinator).As<IMiningFailStopCoordinator>().ExternallyOwned();
            context = builder.Build();
            stratum = new AdmissionProbe(context, Bus);
        }
        public async Task Start() { await Recorder.StartAsync(CancellationToken.None); started = true; }
        public Task Acknowledge(Share share) => stratum.Admit(share, () =>
        { Interlocked.Increment(ref Acknowledgements); return Task.CompletedTask; });
        public async Task Stop()
        {
            if(!started) return;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await Recorder.StopAsync(stop.Token); }
            catch(IOException) when(Coordinator.IsFailStopRequested) { }
            started = false;
        }
        public async ValueTask DisposeAsync()
        {
            await Stop();
            Recorder.Dispose();
            context.Dispose();
            Coordinator.Dispose();
            if(ownsDirectory && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class AdmissionProbe(IComponentContext context, IMessageBus bus) :
        StratumServer(context, bus, new RecyclableMemoryStreamManager(), new StandardClock())
    {
        public Task Admit(Share share, Func<Task> acknowledgement) => PublishShareAndAcknowledgeAsync(share, acknowledgement);
        protected override void OnConnect(StratumConnection connection, IPEndPoint endpoint) => throw new NotSupportedException();
        protected override Task OnRequestAsync(StratumConnection connection, Timestamped<JsonRpcRequest> request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
