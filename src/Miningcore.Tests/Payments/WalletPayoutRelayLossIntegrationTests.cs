using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Tests.Persistence.Postgres;
using Miningcore.Time;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task NativeNonPpsRelayLoss_IsVisibleAgainstDurableReceiverRows()
    {
        foreach(var scheme in new[] { PayoutScheme.PPLNS, PayoutScheme.PROP })
        {
            await using var db = await Database.Create();
            var config = CapacityConfig();
            config.Pools[0].PaymentProcessing.PayoutScheme = scheme;
            config.ClusterName = "native-loss-fixture";
            var url = RelayUrl();
            var sender = new MessageBus();
            using var relay = new ShareRelay(new ClusterConfig
            {
                ClusterName = config.ClusterName, Pools = config.Pools,
                ShareRelay = new() { PublishUrl = url },
            }, sender);
            await relay.StartAsync(CancellationToken.None);
            Share NewShare(string worker) => new()
            {
                PoolId = config.Pools[0].Id, Miner = "native-loss-miner", Worker = worker,
                UserAgent = new string('a', 256), IpAddress = "127.0.0.1", Created = DateTime.UtcNow,
                Difficulty = 1, NetworkDifficulty = 100, BlockHeight = 100,
            };
            async Task Send(string worker)
            {
                sender.SendMessage(NewShare(worker));
                var expected = relay.AcceptedCount;
                await Until(() => Task.FromResult(relay.NativeSendCount == expected), 10);
            }

            // A PUB with no subscribers reports successful native sends. No
            // queue rejection, exception, replay or durable receiver row exists.
            for(var i = 0; i < 32; i++) sender.SendMessage(NewShare("disconnected"));
            await Until(() => Task.FromResult(relay.NativeSendCount == 32), 10);
            config.ShareRelays = new[] { new ShareRelayEndpointConfig { Url = url } };
            await using var recording = new AccountingLab(db, config);
            recording.Recorder.PersistenceQueueCapacity = 65_536;
            using var blocked = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var blocker = recording.Bus.Listen<Share>().Subscribe(share =>
            {
                if(share.Worker != "block") return;
                blocked.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(90)));
            });
            await recording.Start();
            using var receiver = new ShareReceiver(config, new StandardClock(), recording.Bus,
                TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(2));
            await receiver.StartAsync(CancellationToken.None);
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(config.Pools[0]); pool.NetworkStats.Returns(new BlockchainStats());
            pool.ShareMultiplier.Returns(1d);
            recording.Bus.SendMessage(new PoolStatusNotification { Pool = pool, Status = PoolStatus.Online });
            try
            {
                await Until(async () =>
                {
                    await Send("warmup");
                    return await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares") > 0;
                }, 15);
                Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE worker='disconnected'"));
                await Send("block");
                Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)));
                const int offered = 10_000;
                // Controlled application-side stall allows the poller queue,
                // native HWM and TCP buffers to fill; managed queue stays drained.
                for(var i = 0; i < offered; i++)
                {
                    sender.SendMessage(NewShare("congested-" + i));
                    if(i % 64 == 63)
                        await Until(() => Task.FromResult(relay.NativeSendCount == relay.AcceptedCount), 10);
                }
                await Until(() => Task.FromResult(relay.NativeSendCount == relay.AcceptedCount), 10);
                Assert.Equal(relay.AcceptedCount, relay.NativeSendCount);
                Assert.Equal(0, relay.GetRelayQueueMetrics().OverflowCount);
                resume.Set();
                // A persisted tail marker on this FIFO connection proves that
                // surviving congestion messages reached the recorder before stop.
                // Thousands of rows can take longer to drain on a shared CI
                // runner. This is a hang guard, not a persistence throughput SLA.
                await Until(async () =>
                {
                    await Send("tail");
                    return await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE worker='tail'") > 0;
                }, 120);
                await relay.StopAsync(CancellationToken.None);
                await receiver.StopAsync(CancellationToken.None);
                await recording.Stop();
                var persisted = await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE worker LIKE 'congested-%'");
                Assert.InRange(persisted, 1, offered - 1);
                Assert.Equal(persisted, await db.Admin.ExecuteScalarAsync<int>("SELECT count(DISTINCT worker) FROM shares WHERE worker LIKE 'congested-%'"));
                Assert.Equal(0, recording.Recorder.PersistenceQueueOverflowCount);
                Assert.False(recording.Coordinator.IsFailStopRequested);
                output.WriteLine($"{scheme}: disconnected accepted/native=32, persisted=0; stalled receiver offered={offered}, durable unique rows={persisted}, missing={offered - persisted}; 256-byte user agent, native HWM=128, managed rejections=0.");
            }
            finally
            {
                resume.Set();
                await relay.StopAsync(CancellationToken.None);
                await receiver.StopAsync(CancellationToken.None);
            }
        }
    }
}
