using System;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Tests.Util;
using NSubstitute;
using Prometheus;
using Xunit;

namespace Miningcore.Tests;

public class HostedServiceStartupTests
{
    [Fact]
    public async Task StatsRecorder_ReceivesImmediatePoolOnlineAndDisposesSubscription()
    {
        var notifications = new Subject<PoolStatusNotification>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<PoolStatusNotification>().Returns(notifications);
        var recorder = new StatsRecorder(
            Substitute.For<IComponentContext>(),
            new MockMasterClock { CurrentTime = DateTime.UtcNow },
            Substitute.For<IConnectionFactory>(),
            messageBus,
            Substitute.For<IMapper>(),
            new ClusterConfig(),
            Substitute.For<IShareRepository>(),
            Substitute.For<IStatsRepository>());
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(new PoolConfig { Id = "immediate-pool" });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await recorder.StartAsync(stop.Token);
        Assert.True(notifications.HasObservers);

        notifications.OnNext(new PoolStatusNotification
        {
            Pool = pool,
            Status = PoolStatus.Online,
        });

        await WaitUntilAsync(() => recorder.AttachedPoolCount == 1,
            stop.Token);
        await recorder.StopAsync(stop.Token);

        Assert.False(notifications.HasObservers);
    }

    [Fact]
    public async Task MetricsPublisher_AttachesAllSubscriptionsBeforeStartReturns()
    {
        var telemetry = new Subject<TelemetryEvent>();
        var hashrates = new Subject<HashrateNotification>();
        var auxiliaryRpc = new Subject<AuxiliaryTemplateRpcTelemetryEvent>();
        var auxiliaryState = new Subject<AuxiliaryTemplateStateTelemetryEvent>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<TelemetryEvent>().Returns(telemetry);
        messageBus.Listen<HashrateNotification>().Returns(hashrates);
        messageBus.Listen<AuxiliaryTemplateRpcTelemetryEvent>().Returns(auxiliaryRpc);
        messageBus.Listen<AuxiliaryTemplateStateTelemetryEvent>().Returns(auxiliaryState);
        var publisher = new MetricsPublisher(messageBus);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await publisher.StartAsync(stop.Token);

        Assert.True(telemetry.HasObservers);
        Assert.True(hashrates.HasObservers);
        Assert.True(auxiliaryRpc.HasObservers);
        Assert.True(auxiliaryState.HasObservers);
        telemetry.OnNext(new TelemetryEvent("pool", TelemetryCategory.Share,
            TimeSpan.Zero, true));
        hashrates.OnNext(new HashrateNotification { PoolId = "pool", Hashrate = 1 });

        await publisher.StopAsync(stop.Token);

        Assert.False(telemetry.HasObservers);
        Assert.False(hashrates.HasObservers);
        Assert.False(auxiliaryRpc.HasObservers);
        Assert.False(auxiliaryState.HasObservers);
    }

    [Fact]
    public async Task MetricsPublisher_ExportsAuxiliaryTemplateOutcomesAndFallbackState()
    {
        var telemetry = new Subject<TelemetryEvent>();
        var hashrates = new Subject<HashrateNotification>();
        var auxiliaryRpc = new Subject<AuxiliaryTemplateRpcTelemetryEvent>();
        var auxiliaryState = new Subject<AuxiliaryTemplateStateTelemetryEvent>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<TelemetryEvent>().Returns(telemetry);
        messageBus.Listen<HashrateNotification>().Returns(hashrates);
        messageBus.Listen<AuxiliaryTemplateRpcTelemetryEvent>().Returns(auxiliaryRpc);
        messageBus.Listen<AuxiliaryTemplateStateTelemetryEvent>().Returns(auxiliaryState);
        var registry = Metrics.NewCustomRegistry();
        var publisher = new MetricsPublisher(messageBus, null,
            Metrics.WithCustomRegistry(registry), registry);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await publisher.StartAsync(stop.Token);

        auxiliaryRpc.OnNext(new AuxiliaryTemplateRpcTelemetryEvent("doge-solo",
            AuxiliaryTemplateRpcOutcome.Timeout, TimeSpan.FromMilliseconds(1000)));
        auxiliaryRpc.OnNext(new AuxiliaryTemplateRpcTelemetryEvent("doge-solo",
            AuxiliaryTemplateRpcOutcome.Cancellation, TimeSpan.FromMilliseconds(25)));
        auxiliaryState.OnNext(new AuxiliaryTemplateStateTelemetryEvent("doge-solo",
            true, true));

        var degraded = await WaitForMetricsAsync(registry, text =>
            text.Contains("miningcore_auxiliary_template_degraded{pool=\"doge-solo\"} 1") &&
            text.Contains("miningcore_auxiliary_template_rpc_total{pool=\"doge-solo\",outcome=\"timeout\"} 1") &&
            text.Contains("miningcore_auxiliary_template_rpc_total{pool=\"doge-solo\",outcome=\"cancellation\"} 1"),
            stop.Token);

        Assert.Contains(
            "miningcore_auxiliary_template_rpc_duration_ms_sum{pool=\"doge-solo\",outcome=\"timeout\"} 1000",
            degraded);
        Assert.Contains(
            "miningcore_auxiliary_template_rpc_duration_ms_count{pool=\"doge-solo\",outcome=\"timeout\"} 1",
            degraded);
        Assert.Contains(
            "miningcore_auxiliary_template_fallback_total{pool=\"doge-solo\"} 1",
            degraded);

        auxiliaryState.OnNext(new AuxiliaryTemplateStateTelemetryEvent("doge-solo",
            false, false));
        var recovered = await WaitForMetricsAsync(registry, text =>
            text.Contains("miningcore_auxiliary_template_degraded{pool=\"doge-solo\"} 0"),
            stop.Token);

        Assert.Contains(
            "miningcore_auxiliary_template_fallback_total{pool=\"doge-solo\"} 1",
            recovered);
        await publisher.StopAsync(stop.Token);
    }

    [Fact]
    public async Task MetricsPublisher_ExportsSharePersistenceQueueMetrics()
    {
        var provider = Substitute.For<ISharePersistenceQueueMetricsProvider>();
        provider.GetPersistenceQueueMetrics().Returns(
            new SharePersistenceQueueMetricsSnapshot(17, 41, 65_536, 3));
        provider.GetEmergencyJournalQueueMetrics().Returns(
            new SharePersistenceQueueMetricsSnapshot(2, 7, 1_024, 5));
        var registry = Metrics.NewCustomRegistry();
        _ = new MetricsPublisher(Substitute.For<IMessageBus>(), provider,
            Metrics.WithCustomRegistry(registry), registry);
        await using var stream = new MemoryStream();

        await registry.CollectAndExportAsTextAsync(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains(
            "miningcore_share_persistence_queue_depth{queue=\"primary\"} 17",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_high_watermark{queue=\"primary\"} 41",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_capacity{queue=\"primary\"} 65536",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_depth{queue=\"emergency_journal\"} 2",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_high_watermark{queue=\"emergency_journal\"} 7",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_capacity{queue=\"emergency_journal\"} 1024",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_overflow_total{queue=\"primary\"} 3",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_overflow_total{queue=\"emergency_journal\"} 5",
            text);
    }

    [Fact]
    public async Task MetricsPublisher_MissingRecorderProviderExportsNoQueueSeries()
    {
        var registry = Metrics.NewCustomRegistry();
        _ = new MetricsPublisher(Substitute.For<IMessageBus>(), null,
            Metrics.WithCustomRegistry(registry), registry);
        await using var stream = new MemoryStream();

        await registry.CollectAndExportAsTextAsync(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain(
            "miningcore_share_persistence_queue_depth{queue=", text);
        Assert.DoesNotContain(
            "miningcore_share_persistence_queue_high_watermark{queue=", text);
        Assert.DoesNotContain(
            "miningcore_share_persistence_queue_capacity{queue=", text);
        Assert.DoesNotContain(
            "miningcore_share_persistence_queue_overflow_total{queue=", text);
    }

    [Fact]
    public async Task MetricsPublisher_RepeatedConcurrentScrapesDoNotDoubleCountOverflow()
    {
        var provider = Substitute.For<ISharePersistenceQueueMetricsProvider>();
        provider.GetPersistenceQueueMetrics().Returns(
            new SharePersistenceQueueMetricsSnapshot(0, 8, 65_536, 9));
        provider.GetEmergencyJournalQueueMetrics().Returns(
            new SharePersistenceQueueMetricsSnapshot(0, 2, 1_024, 11));
        var registry = Metrics.NewCustomRegistry();
        _ = new MetricsPublisher(Substitute.For<IMessageBus>(), provider,
            Metrics.WithCustomRegistry(registry), registry);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var concurrentStream = new MemoryStream();
            await registry.CollectAndExportAsTextAsync(concurrentStream);
        }));
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains(
            "miningcore_share_persistence_queue_overflow_total{queue=\"primary\"} 9",
            text);
        Assert.Contains(
            "miningcore_share_persistence_queue_overflow_total{queue=\"emergency_journal\"} 11",
            text);
    }

    [Fact]
    public async Task MetricsPublisher_ScrapesRemainConsistentWhileQueueIsActive()
    {
        const int capacity = 8;
        const int producerCount = 4;
        const int itemsPerProducer = 100;
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        var primary = new BoundedQueueAccounting<int>(capacity);
        var emergency = new BoundedQueueAccounting<int>(1);
        var provider = new LiveQueueMetricsProvider(primary, emergency);
        var registry = Metrics.NewCustomRegistry();
        _ = new MetricsPublisher(Substitute.For<IMessageBus>(), provider,
            Metrics.WithCustomRegistry(registry), registry);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var producers = Enumerable.Range(0, producerCount).Select(producer =>
            Task.Run(async () =>
            {
                for(var i = 0; i < itemsPerProducer; i++)
                {
                    while(!primary.TryWrite(channel.Writer,
                        producer * itemsPerProducer + i))
                    {
                        await Task.Yield();
                    }
                }
            }, stop.Token)).ToArray();

        await WaitUntilAsync(() => primary.HighWatermark == capacity,
            stop.Token);
        var consumer = Task.Run(async () =>
        {
            while(await channel.Reader.WaitToReadAsync(stop.Token))
            {
                while(primary.TryRead(channel.Reader, out _))
                    await Task.Yield();
            }
        }, stop.Token);
        var scrapes = Task.Run(async () =>
        {
            while(producers.Any(x => !x.IsCompleted))
            {
                await using var stream = new MemoryStream();
                await registry.CollectAndExportAsTextAsync(stream, stop.Token);
                var snapshot = primary.GetSnapshot();
                Assert.InRange(snapshot.Depth, 0, snapshot.Capacity);
                Assert.InRange(snapshot.HighWatermark, snapshot.Depth,
                    snapshot.Capacity);
            }
        }, stop.Token);

        await Task.WhenAll(producers);
        channel.Writer.Complete();
        await Task.WhenAll(consumer, scrapes);
        await using var finalStream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(finalStream, stop.Token);
        var text = Encoding.UTF8.GetString(finalStream.ToArray());
        var finalSnapshot = primary.GetSnapshot();

        Assert.Equal(0, finalSnapshot.Depth);
        Assert.Equal(capacity, finalSnapshot.HighWatermark);
        Assert.True(finalSnapshot.OverflowCount > 0);
        Assert.Contains(
            $"miningcore_share_persistence_queue_overflow_total{{queue=\"primary\"}} {finalSnapshot.OverflowCount}",
            text);
    }

    [Fact]
    public async Task NotificationService_AttachesAllSubscriptionsBeforeStartReturns()
    {
        var admin = new Subject<AdminNotification>();
        var blocks = new Subject<BlockFoundNotification>();
        var payments = new Subject<PaymentNotification>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<AdminNotification>().Returns(admin);
        messageBus.Listen<BlockFoundNotification>().Returns(blocks);
        messageBus.Listen<PaymentNotification>().Returns(payments);
        var service = new NotificationService(new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            Notifications = new NotificationsConfig
            {
                Admin = new AdminNotifications { Enabled = true },
                Email = new EmailSenderConfig(),
                Pushover = new PushoverConfig(),
            },
        }, null, messageBus);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(stop.Token);

        Assert.True(admin.HasObservers);
        Assert.True(blocks.HasObservers);
        Assert.True(payments.HasObservers);
        admin.OnNext(new AdminNotification("startup", "immediate"));

        await service.StopAsync(stop.Token);

        Assert.False(admin.HasObservers);
        Assert.False(blocks.HasObservers);
        Assert.False(payments.HasObservers);
    }

    [Fact]
    public async Task CanceledStartup_DoesNotAttachNotificationSubscriptions()
    {
        var admin = new Subject<AdminNotification>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<AdminNotification>().Returns(admin);
        var service = new NotificationService(new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            Notifications = new NotificationsConfig
            {
                Admin = new AdminNotifications { Enabled = true },
                Email = new EmailSenderConfig(),
                Pushover = new PushoverConfig(),
            },
        }, null, messageBus);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.StartAsync(canceled.Token));

        Assert.False(admin.HasObservers);
        messageBus.DidNotReceive().Listen<AdminNotification>();
    }

    [Fact]
    public async Task StartupGate_RejectsExecutionThatCompletesBeforeReadinessSignal()
    {
        var service = new CompletesBeforeReadyService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Contains(nameof(CompletesBeforeReadyService), ex.Message);
    }

    private sealed class CompletesBeforeReadyService : StartupGatedBackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class LiveQueueMetricsProvider(
        BoundedQueueAccounting<int> primary,
        BoundedQueueAccounting<int> emergency) :
        ISharePersistenceQueueMetricsProvider
    {
        public SharePersistenceQueueMetricsSnapshot GetPersistenceQueueMetrics()
        {
            return primary.GetSnapshot();
        }

        public SharePersistenceQueueMetricsSnapshot GetEmergencyJournalQueueMetrics()
        {
            return emergency.GetSnapshot();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while(!condition())
            await Task.Delay(10, ct);
    }

    private static async Task<string> WaitForMetricsAsync(CollectorRegistry registry,
        Func<string, bool> condition, CancellationToken ct)
    {
        while(true)
        {
            await using var stream = new MemoryStream();
            await registry.CollectAndExportAsTextAsync(stream, ct);
            var text = Encoding.UTF8.GetString(stream.ToArray());

            if(condition(text))
                return text;

            await Task.Delay(10, ct);
        }
    }
}
