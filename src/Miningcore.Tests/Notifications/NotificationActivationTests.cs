using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Pushover;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Notifications;

public class NotificationActivationTests
{
    [Theory]
    [InlineData(false, false, null, false, true)]
    [InlineData(true, false, "operator@example.invalid", true, true)]
    [InlineData(false, true, "operator@example.invalid", false, true)]
    [InlineData(true, true, null, true, true)]
    [InlineData(false, true, "operator@example.invalid", true, true)]
    [InlineData(true, true, null, false, false)]
    [InlineData(false, true, " \t ", false, false)]
    public void ValidationUsesAdminAndTransportFlags_NotDeprecatedTopLevel(
        bool topLevel, bool admin, string recipient, bool push, bool valid)
    {
        var config = new NotificationsConfig
        {
            Enabled = topLevel,
            Admin = new AdminNotifications { Enabled = admin, EmailAddress = recipient },
            Email = ValidEmail(),
            Pushover = new PushoverConfig
            {
                Enabled = push, User = "test-user", Token = "test-token",
            },
        };

        Assert.Equal(valid, new NotificationsConfigValidator().Validate(config).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task EnabledCriticalWithoutTransportReportsFailure(string recipient)
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true, EmailAddress = recipient },
        });
        using var service = new NotificationService(config, null,
            Substitute.For<IMessageBus>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendCriticalAdminNotificationAsync(
                new AdminNotification("incident", "durability failure"),
                CancellationToken.None));
        Assert.Contains("no configured", error.Message);
    }

    [Fact]
    public void NormalValidationChecksActiveProvider_RecoveryValidationSkipsIt()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
        });

        Assert.Contains(new ClusterConfigValidator().Validate(config).Errors,
            error => error.ErrorMessage.Contains("notifications.email"));
        Assert.DoesNotContain(new ClusterConfigValidator(true).Validate(config).Errors,
            error => error.ErrorMessage.Contains("notifications.email"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task UnhostedPushoverOnlyCriticalDeliveryAcceptsBlankEmail(string recipient)
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true, EmailAddress = recipient },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var requests = new RecordingPushoverHandler();
        using var client = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());

        await sender.SendCriticalAdminNotificationAsync(
            new AdminNotification("incident", "failure"), CancellationToken.None);

        Assert.Equal(1, requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task PushoverOnlyHostDeliversAdminBlockAndPaymentWithoutEmail(
        string recipient)
    {
        var config = Config(new NotificationsConfig
        {
            Enabled = false,
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = recipient,
                NotifyBlockFound = true, NotifyPaymentSuccess = true,
            },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig
            {
                Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" },
            },
        };
        var admin = new Subject<AdminNotification>();
        var blocks = new Subject<BlockFoundNotification>();
        var payments = new Subject<PaymentNotification>();
        var bus = Substitute.For<IMessageBus>();
        bus.Listen<AdminNotification>().Returns(admin);
        bus.Listen<BlockFoundNotification>().Returns(blocks);
        bus.Listen<PaymentNotification>().Returns(payments);
        var requests = new RecordingPushoverHandler();
        using var client = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await service.StartAsync(timeout.Token).WaitAsync(timeout.Token);
        Assert.True(admin.HasObservers);
        Assert.True(blocks.HasObservers);
        Assert.True(payments.HasObservers);
        admin.OnNext(new AdminNotification("admin", "message"));
        blocks.OnNext(new BlockFoundNotification { PoolId = "test", BlockHeight = 1 });
        payments.OnNext(new PaymentNotification("test", "failed", 1, "BTC"));
        while(requests.Count < 3)
            await Task.Delay(10, timeout.Token);
        await service.StopAsync(timeout.Token);

        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task UnhostedCriticalSenderAttemptsPushAfterEmailFailure()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
            Email = ValidEmail(),
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var requests = new RecordingPushoverHandler();
        using var client = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            sender.SendCriticalAdminNotificationAsync(
                new AdminNotification("incident", "failure"), timeout.Token));

        Assert.Single(error.InnerExceptions);
        Assert.Contains("email", error.InnerExceptions[0].Message);
        Assert.Equal(1, requests.Count);
    }

    [Fact]
    public async Task UnhostedCriticalSenderReportsBothTransportFailures()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
            Email = ValidEmail(),
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var requests = new RecordingPushoverHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            sender.SendCriticalAdminNotificationAsync(
                new AdminNotification("incident", "failure"), timeout.Token));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(1, requests.Count);
    }

    [Fact]
    public async Task StalledSmtpDoesNotDelayPushoverCriticalAttempt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) listener.LocalEndpoint).Port;
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
            Email = email,
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var requests = new RecordingPushoverHandler();
        using var client = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = listener.AcceptTcpClientAsync(timeout.Token).AsTask();

        var delivery = sender.SendCriticalAdminNotificationAsync(
            new AdminNotification("incident", "failure"), timeout.Token);
        using var peer = await accepted.WaitAsync(timeout.Token);
        while(requests.Count == 0)
            await Task.Delay(10, timeout.Token);
        Assert.False(delivery.IsCompleted);

        timeout.Cancel();
        var error = await Assert.ThrowsAsync<AggregateException>(() => delivery);
        Assert.Single(error.InnerExceptions);
        Assert.Equal(1, requests.Count);
    }

    [Theory]
    [InlineData(null, "test-token")]
    [InlineData("test-user", null)]
    [InlineData(" ", "test-token")]
    [InlineData("test-user", " ")]
    public void EnabledPushoverRequiresNonblankCredentials(string user, string token)
    {
        var config = new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = user, Token = token,
            },
        };

        Assert.False(new NotificationsConfigValidator().Validate(config).IsValid);
    }

    [Fact]
    public async Task MissingCriticalTransportDoesNotReplaceCandidateDurabilityFailure()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true, EmailAddress = " \t " },
        });
        using var sender = new NotificationService(config, null,
            Substitute.For<IMessageBus>());
        var status = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("test.fatal");
        var handler = new CandidatePersistenceFailureHandler(
            new MiningFailStopCoordinator(status, lifetime),
            new Lazy<ICriticalNotificationSender>(() => sender), fatalState);
        var databaseFailure = new IOException("database unavailable");
        var journalFailure = new IOException("journal unavailable");

        await handler.StopClusterAsync(new[]
        {
            new Miningcore.Blockchain.Share
            {
                PoolId = "test", Miner = "operator", BlockHeight = 1,
            },
        }, databaseFailure, journalFailure, false);

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            status.ExitCode);
        lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task InactiveTopLevelConflictHasNoHostedSubscriptionsOrCriticalDelivery()
    {
        var config = Config(new NotificationsConfig
        {
            Enabled = true,
            Admin = new AdminNotifications
            {
                Enabled = false, EmailAddress = "operator@example.invalid",
            },
            Pushover = new PushoverConfig { Enabled = true },
        });
        var bus = Substitute.For<IMessageBus>();
        var factory = Substitute.For<IHttpClientFactory>();
        using var client = new HttpClient();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        using var host = new HostBuilder()
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton(bus);
                services.AddSingleton(factory);
                services.AddHostedService<NotificationService>();
            })
            .ConfigureContainer<ContainerBuilder>(builder =>
            {
                builder.RegisterType<PushoverClient>().SingleInstance();
                builder.RegisterType<NotificationService>()
                    .AsSelf().As<ICriticalNotificationSender>().SingleInstance();
            })
            .Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var hosted = Assert.IsType<NotificationService>(
            Assert.Single(host.Services.GetServices<IHostedService>()));
        var critical = host.Services.GetRequiredService<ICriticalNotificationSender>();
        Assert.NotSame(hosted, critical);
        await host.StartAsync(timeout.Token).WaitAsync(timeout.Token);
        await critical.SendCriticalAdminNotificationAsync(
            new AdminNotification("test", "disabled"), timeout.Token);
        await host.StopAsync(timeout.Token);

        bus.DidNotReceive().Listen<AdminNotification>();
        bus.DidNotReceive().Listen<BlockFoundNotification>();
        bus.DidNotReceive().Listen<PaymentNotification>();
    }

    private static ClusterConfig Config(NotificationsConfig notifications) => new()
    {
        Pools = Array.Empty<PoolConfig>(),
        Notifications = notifications,
    };

    private static EmailSenderConfig ValidEmail() => new()
    {
        Host = "127.0.0.1", Port = 1, TlsMode = SmtpTlsMode.StartTls,
        User = "test-user",
        Password = "test-password", FromName = "Operator",
        FromAddress = "pool@example.invalid",
    };

    private sealed class RecordingPushoverHandler(
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Interlocked.Increment(ref count);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"status\":1}"),
            });
        }
    }
}
