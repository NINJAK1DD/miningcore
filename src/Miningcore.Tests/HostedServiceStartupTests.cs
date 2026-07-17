using System;
using System.Reactive.Subjects;
using System.Threading;
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
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<TelemetryEvent>().Returns(telemetry);
        messageBus.Listen<HashrateNotification>().Returns(hashrates);
        var publisher = new MetricsPublisher(messageBus);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await publisher.StartAsync(stop.Token);

        Assert.True(telemetry.HasObservers);
        Assert.True(hashrates.HasObservers);
        telemetry.OnNext(new TelemetryEvent("pool", TelemetryCategory.Share,
            TimeSpan.Zero, true));
        hashrates.OnNext(new HashrateNotification { PoolId = "pool", Hashrate = 1 });

        await publisher.StopAsync(stop.Token);

        Assert.False(telemetry.HasObservers);
        Assert.False(hashrates.HasObservers);
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

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while(!condition())
            await Task.Delay(10, ct);
    }
}
