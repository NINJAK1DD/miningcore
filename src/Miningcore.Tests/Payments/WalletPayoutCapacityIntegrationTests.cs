using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Time;
using NSubstitute;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    private static ClusterConfig CapacityConfig() => new()
    {
        PaymentProcessing = new() { Enabled = true, Paused = true },
        Pools = new[] { new PoolConfig { Id = "capacity-pps", Enabled = true, Coin = "bitcoin",
            Template = ModuleInitializer.CoinTemplates["bitcoin"], RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new() { Enabled = true, PayoutScheme = PayoutScheme.PPS } } },
    };

    private static string RelayUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return $"tcp://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}";
    }

    [WalletControlFact]
    public Task PpsCapacity_Sustains1000PerSecondAndRecovers10000ShareBurst() => PpsCapacity(false);

    [WalletControlFact]
    public Task PpsRelayCapacity_Sustains1000PerSecondAndRecovers10000ShareBurst() => PpsCapacity(true);

    private async Task PpsCapacity(bool relayed)
    {
        await using var db = await Database.Create();
        await db.Admin.ExecuteAsync($"ALTER ROLE {db.AppRole} SET track_io_timing=on; ALTER ROLE {db.AppRole} SET track_wal_io_timing=on");
        var config = CapacityConfig();
        config.ClusterName = "capacity-sender";
        if(relayed) config.ShareRelay = new() { PublishUrl = RelayUrl(), SharedEncryptionKey = RelayTestKey };
        await using var lab = new AccountingLab(db, config);
        lab.Recorder.PersistenceQueueCapacity = 65_536; // Production default.
        await lab.Start();
        await using var transport = relayed ? await RelayCapacityLab.Start(db, config, lab) : null;
        Share NewShare() { var share = PpsShare(config.Pools[0]); share.Source = config.ClusterName; return share; }
        await lab.Acknowledge(NewShare()); // Warm database and serializer.
        foreach(var burst in new[] { false, true })
        {
            var count = burst ? 10_000 : 60_000;
            var latencies = new double[count];
            var responses = new Task[count];
            await db.Admin.ExecuteAsync("SELECT pg_stat_clear_snapshot()");
            var before = await db.Admin.QuerySingleAsync<(long Commits, string Lsn)>(
                "SELECT xact_commit,pg_current_wal_lsn()::text FROM pg_stat_database WHERE datname=current_database()");
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();
            for(var i = 0; i < count; i++)
            {
                if(!burst)
                {
                    var delay = TimeSpan.FromMilliseconds(i) - elapsed.Elapsed;
                    if(delay > TimeSpan.Zero) await Task.Delay(delay);
                }
                var index = i;
                var start = Stopwatch.GetTimestamp();
                responses[index] = Acknowledge();
                async Task Acknowledge()
                {
                    await lab.Acknowledge(NewShare());
                    latencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
            }
            var offeredSeconds = elapsed.Elapsed.TotalSeconds;
            if(!burst) Assert.InRange(offeredSeconds, 59.5, 66);
            await Task.WhenAll(responses).WaitAsync(TimeSpan.FromSeconds(60));
            var recoverySeconds = elapsed.Elapsed.TotalSeconds - offeredSeconds;
            await db.Admin.ExecuteAsync("SELECT pg_stat_clear_snapshot()");
            var after = await db.Admin.QuerySingleAsync<(long Commits, decimal WalBytes)>(
                "SELECT xact_commit,pg_wal_lsn_diff(pg_current_wal_lsn(),CAST(@lsn AS pg_lsn)) FROM pg_stat_database WHERE datname=current_database()",
                new { lsn = before.Lsn });
            Array.Sort(latencies);
            Assert.Equal(burst ? 70_001 : 60_001, lab.Acknowledgements);
            Assert.Equal(lab.Acknowledgements, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
            Assert.Equal(lab.Acknowledgements, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
            Assert.Equal(lab.Acknowledgements * PpsShare(config.Pools[0]).PpsCalculatedAmount.Value,
                await db.Admin.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances"));
            Assert.Equal(0, lab.Recorder.PersistenceQueueOverflowCount);
            Assert.Equal(0, lab.Recorder.EmergencyJournalQueueOverflowCount);
            Assert.False(File.Exists(config.ShareRecoveryFile));
            Assert.False(lab.Coordinator.IsFailStopRequested);
            if(transport != null)
            {
                Assert.Equal(0, transport.Relay.GetRelayQueueMetrics().OverflowCount);
                transport.AssertHealthy();
                output.WriteLine($"Relay queue: {transport.Relay.GetRelayQueueMetrics()}; queued bytes={transport.Relay.QueuedBytes}/{transport.Relay.ByteCapacity}.");
            }
            output.WriteLine($"PPS {(relayed ? "relay" : "local")} {(burst ? "burst" : "sustained")}: shares={count}; offered seconds={offeredSeconds:F3}; recovery seconds={recoverySeconds:F3}; ack p95={latencies[(int)(count*.95)]:F3}ms p99={latencies[(int)(count*.99)]:F3}ms max={latencies[^1]:F3}ms; commits={after.Commits-before.Commits}; WAL bytes={after.WalBytes}; managed CPU seconds={(process.TotalProcessorTime-cpu).TotalSeconds:F3}; queue high-water={lab.Recorder.PersistenceQueueHighWatermark}/65536; queue depth={lab.Recorder.PersistenceQueueDepth}.");
            output.WriteLine("PostgreSQL cumulative WAL statistics: " + await db.Admin.ExecuteScalarAsync<string>("SELECT row_to_json(w)::text FROM pg_stat_wal w"));
            if(await db.Admin.ExecuteScalarAsync<bool>("SELECT to_regclass('pg_stat_io') IS NOT NULL"))
                output.WriteLine("PostgreSQL cumulative client I/O statistics: " + await db.Admin.ExecuteScalarAsync<string>(
                    "SELECT json_agg(i)::text FROM pg_stat_io i WHERE backend_type='client backend' AND context='normal'"));
        }
    }

    private sealed class RelayCapacityLab : IAsyncDisposable
    {
        public ShareRelay Relay;
        private ShareReceiver receiver;
        private AccountingLab accounting;
        public void AssertHealthy()
        {
            Assert.False(accounting.Coordinator.IsFailStopRequested);
            Assert.Equal(0, accounting.Recorder.PersistenceQueueOverflowCount);
            Assert.Equal(0, accounting.Recorder.EmergencyJournalQueueOverflowCount);
        }
        public static async Task<RelayCapacityLab> Start(Database db, ClusterConfig senderConfig, AccountingLab sender)
        {
            var result = new RelayCapacityLab();
            try
            {
                var config = JsonConvert.DeserializeObject<ClusterConfig>(JsonConvert.SerializeObject(senderConfig));
                config.Pools[0].Template = senderConfig.Pools[0].Template;
                config.ShareRelay = null;
                config.ShareRelays = new[] { new ShareRelayEndpointConfig { Url = senderConfig.ShareRelay.PublishUrl, SharedEncryptionKey = RelayTestKey } };
                result.accounting = new AccountingLab(db, config);
                result.accounting.Recorder.PersistenceQueueCapacity = 65_536;
                await result.accounting.Start();
                result.receiver = new ShareReceiver(config, new StandardClock(), result.accounting.Bus,
                    TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(15));
                var pool = Substitute.For<IMiningPool>(); pool.Config.Returns(config.Pools[0]);
                pool.NetworkStats.Returns(new BlockchainStats()); pool.ShareMultiplier.Returns(1d);
                await result.receiver.StartAsync(CancellationToken.None);
                result.accounting.Bus.SendMessage(new PoolStatusNotification { Pool = pool, Status = PoolStatus.Online });
                await Until(() => Task.FromResult(result.receiver.AttachedPoolCount == 1), 5);
                result.Relay = new ShareRelay(senderConfig, sender.Bus, sender.Coordinator);
                await result.Relay.StartAsync(CancellationToken.None);
                await Task.Delay(300);
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if(Relay != null) { await Relay.StopAsync(CancellationToken.None); Relay.Dispose(); }
            if(receiver != null) { await receiver.StopAsync(CancellationToken.None); receiver.Dispose(); }
            if(accounting != null) await accounting.DisposeAsync();
        }
    }

    [WalletControlFact]
    public async Task PpsHealthySlowDatabase_KeepsEveryResponsePendingUntilCommit()
    {
        await using var db = await Database.Create();
        var config = CapacityConfig();
        await using var lab = new AccountingLab(db, config);
        await lab.Start();
        await db.Admin.ExecuteAsync("BEGIN; LOCK TABLE pps_share_credits IN ACCESS EXCLUSIVE MODE");
        var responses = Enumerable.Range(0, 100).Select(_ => lab.Acknowledge(PpsShare(config.Pools[0]))).ToArray();
        await Task.Delay(300);
        Assert.All(responses, response => Assert.False(response.IsCompleted));
        Assert.Equal(0, lab.Acknowledgements);
        await db.Admin.ExecuteAsync("COMMIT");
        await Task.WhenAll(responses).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(100, lab.Acknowledgements);
        Assert.Equal(100, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
        Assert.False(lab.Coordinator.IsFailStopRequested);
    }

    [WalletControlFact]
    public async Task PpsRelayOverload_BoundsMemoryAndPreservesAcknowledgedLiabilities()
    {
        await using var db = await Database.Create();
        var config = CapacityConfig();
        config.ShareRelay = new() { PublishUrl = RelayUrl(), SharedEncryptionKey = RelayTestKey };
        await using var lab = new AccountingLab(db, config);
        await lab.Start();
        using var sending = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var relay = new ShareRelay(config, lab.Bus, lab.Coordinator)
        {
            QueueCapacity = 8,
            BeforeSend = ct => { sending.Set(); resume.Wait(ct); },
        };
        await relay.StartAsync(CancellationToken.None);
        var shares = new List<Share>();
        try
        {
            var first = PpsShare(config.Pools[0]); shares.Add(first);
            await lab.Acknowledge(first);
            Assert.True(sending.Wait(TimeSpan.FromSeconds(5)));
            for(var i = 0; i < 8; i++)
            {
                var share = PpsShare(config.Pools[0]); shares.Add(share);
                await lab.Acknowledge(share);
            }
            Assert.Equal(8, relay.GetRelayQueueMetrics().Depth);
            Assert.InRange(relay.QueuedBytes, 1, relay.ByteCapacity);
            await Assert.ThrowsAnyAsync<IOException>(() => lab.Acknowledge(PpsShare(config.Pools[0])));
            Assert.True(lab.Coordinator.IsFailStopRequested);
            Assert.Equal(9, lab.Acknowledgements);
            Assert.Equal(1, relay.GetRelayQueueMetrics().OverflowCount);
            Assert.Equal(8, relay.GetRelayQueueMetrics().HighWatermark);
            // A PUB/SUB receiver can be absent throughout: local PostgreSQL owns
            // every positive response, independently of network delivery.
            Assert.Equal(9, await db.Admin.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_share_credits WHERE accountingid=ANY(@ids)",
                new { ids = shares.Select(x => Guid.Parse(x.AccountingId)).ToArray() }));
        }
        finally { resume.Set(); await relay.StopAsync(CancellationToken.None); }
        await lab.Stop();
        var credits = await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits");
        await using var recovered = new AccountingLab(db, config, reuseDirectory: true);
        await recovered.Start();
        await Task.WhenAll(shares.Select(recovered.Acknowledge)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(credits, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
    }

    [WalletControlFact]
    public async Task PpsRelayAuthentication_RejectsImpersonatingPublisherAndAcceptsPinnedKey()
    {
        await using var db = await Database.Create();
        var config = CapacityConfig();
        var url = RelayUrl();
        config.ShareRelays = new[] { new ShareRelayEndpointConfig { Url = url, SharedEncryptionKey = RelayTestKey } };
        await using var lab = new AccountingLab(db, config);
        await lab.Start();
        using var receiver = new ShareReceiver(config, new StandardClock(), lab.Bus,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config.Pools[0]); pool.NetworkStats.Returns(new BlockchainStats()); pool.ShareMultiplier.Returns(1d);
        await receiver.StartAsync(CancellationToken.None);
        lab.Bus.SendMessage(new PoolStatusNotification { Pool = pool, Status = PoolStatus.Online });
        await Until(() => Task.FromResult(receiver.AttachedPoolCount == 1), 5);
        var observed = new List<Share>();
        using var subscription = lab.Bus.Listen<Share>().Subscribe(share => { lock(observed) observed.Add(share); });
        foreach(var authorized in new[] { false, true })
        {
            var bus = new MessageBus();
            using var relay = new ShareRelay(new ClusterConfig { ShareRelay = new() { PublishUrl = url,
                SharedEncryptionKey = authorized ? RelayTestKey : "untrusted-publisher-key-32-characters-minimum" } }, bus);
            await relay.StartAsync(CancellationToken.None);
            var share = PpsShare(config.Pools[0]);
            for(var i = 0; i < 30; i++) { bus.SendMessage(share); await Task.Delay(100); }
            await relay.StopAsync(CancellationToken.None);
            if(!authorized)
            {
                lock(observed) Assert.Empty(observed);
                Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits"));
            }
            else
            {
                await Until(async () => await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pps_share_credits") == 1, 15);
                lock(observed) Assert.All(observed, value => Assert.True(value.IsRelayed));
            }
        }
        await receiver.StopAsync(CancellationToken.None);
    }
}
