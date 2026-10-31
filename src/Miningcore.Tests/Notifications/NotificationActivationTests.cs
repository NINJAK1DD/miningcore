using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
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
using NLog;
using NLog.Config;
using NLog.Targets;
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
    [InlineData("not-an-address", "pool@example.invalid", "admin.emailAddress")]
    [InlineData("operator@example.invalid", "not-an-address", "email.fromAddress")]
    [InlineData(" operator@example.invalid ", "pool@example.invalid", "admin.emailAddress")]
    public void ActiveEmailRequiresPlainAddressSyntax(string recipient,
        string senderAddress, string expectedPath)
    {
        var email = ValidEmail();
        email.FromAddress = senderAddress;
        var config = new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = recipient,
            },
            Email = email,
        };

        var result = new NotificationsConfigValidator().Validate(config);
        Assert.Contains(result.Errors, error =>
            error.ErrorMessage.Contains(expectedPath));
    }

    [Fact]
    public void ValidationReportsEmailAndPushoverFailuresTogether()
    {
        var email = ValidEmail();
        email.TlsMode = null;
        email.FromName = " \t ";
        email.FromAddress = "not-an-address";
        var config = new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
            Email = email,
            Pushover = new PushoverConfig { Enabled = true },
        };

        var errors = new NotificationsConfigValidator().Validate(config).Errors;
        Assert.Contains(errors, error => error.ErrorMessage.Contains("tlsMode"));
        Assert.Contains(errors, error => error.ErrorMessage.Contains("email.fromName"));
        Assert.Contains(errors, error => error.ErrorMessage.Contains("email.fromAddress"));
        Assert.Contains(errors, error => error.ErrorMessage.Contains("pushover.user"));
        Assert.Contains(errors, error => error.ErrorMessage.Contains("pushover.token"));
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
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) smtp.LocalEndpoint).Port;
        var config = Config(new NotificationsConfig
        {
            Enabled = false,
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = recipient,
                NotifyBlockFound = true, NotifyPaymentSuccess = true,
            },
            // A valid provider must still receive no connection for blank recipients.
            Email = email,
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
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger);
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
        Assert.False(smtp.Pending());
        Assert.Empty(logs.Target.Logs);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("block")]
    [InlineData("payment")]
    public async Task NonblankRecipientAttemptsEmailAndPushWithoutWaitingForSmtp(
        string eventType)
    {
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) smtp.LocalEndpoint).Port;
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
                NotifyBlockFound = true, NotifyPaymentSuccess = true,
            },
            Email = email,
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
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = smtp.AcceptTcpClientAsync(timeout.Token).AsTask();

        await service.StartAsync(timeout.Token).WaitAsync(timeout.Token);
        switch(eventType)
        {
            case "admin":
                admin.OnNext(new AdminNotification("incident", "message"));
                break;
            case "block":
                blocks.OnNext(new BlockFoundNotification
                {
                    PoolId = "test", BlockHeight = 1,
                });
                break;
            default:
                payments.OnNext(new PaymentNotification("test", "failed", 1, "BTC"));
                break;
        }

        using var peer = await accepted.WaitAsync(timeout.Token);
        while(requests.Count == 0)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(1, requests.Count);
        Assert.False(smtp.Pending());
        await service.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task PushoverOnlyUncertainPayoutNeverPointsToEmail()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
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
        var payments = new Subject<PaymentNotification>();
        var bus = Substitute.For<IMessageBus>();
        bus.Listen<AdminNotification>().Returns(new Subject<AdminNotification>());
        bus.Listen<BlockFoundNotification>().Returns(new Subject<BlockFoundNotification>());
        bus.Listen<PaymentNotification>().Returns(payments);
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(timeout.Token).WaitAsync(timeout.Token);
        payments.OnNext(new PaymentNotification("test", "private failure", 1, "BTC")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
        });
        while(requests.Count == 0)
            await Task.Delay(10, timeout.Token);
        await service.StopAsync(timeout.Token);

        using var document = JsonDocument.Parse(requests.LastBody);
        var message = document.RootElement.GetProperty("message").GetString();
        Assert.Contains("retained private payout reconciliation evidence", message);
        Assert.DoesNotContain("email", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealMessageBusPublisherDoesNotWaitForPayoutFormatting()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
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
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        service.BeforePaymentFormat = _ =>
        {
            entered.Set();
            release.Wait(deadline.Token);
        };

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        var publish = Task.Run(() => bus.SendMessage(
            new PaymentNotification("test", "private failure", 1, "BTC")
            {
                Outcome = PaymentNotificationOutcome.Uncertain,
            }));
        try
        {
            await Task.Run(() => entered.Wait(deadline.Token), deadline.Token);
            await publish.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, requests.Count);
        }
        finally
        {
            release.Set();
            await publish.WaitAsync(deadline.Token);
        }

        while(requests.Count == 0)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task BoundedFormattingQueueReportsEveryDroppedPayoutIncident()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
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
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger)
        {
            MaxPendingOrdinaryNotifications = 2,
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        service.BeforePaymentFormat = _ =>
        {
            entered.Set();
            release.Wait(deadline.Token);
        };

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        try
        {
            bus.SendMessage(new PaymentNotification("test", "private failure", 1, "BTC")
            {
                Outcome = PaymentNotificationOutcome.Uncertain,
            });
            await Task.Run(() => entered.Wait(deadline.Token), deadline.Token);
            for(var index = 0; index < 4; index++)
                bus.SendMessage(new PaymentNotification("test", "private failure", 1, "BTC")
                {
                    Outcome = PaymentNotificationOutcome.Uncertain,
                });

            Assert.Equal(2, service.PendingFormatting);
            Assert.Equal(3, service.DroppedFormatting);
            Assert.Equal(3, logs.Target.Logs.Count);
            Assert.All(logs.Target.Logs, entry =>
            {
                Assert.Contains("payout failure or uncertain outcome not queued", entry);
                Assert.Contains("pool test", entry);
            });
        }
        finally
        {
            release.Set();
        }

        while(requests.Count < 2)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task MalformedPublisherEventDoesNotStopLaterAlerts()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig { Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" } },
        };
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        bus.SendMessage<PaymentNotification>(null);
        bus.SendMessage(new PaymentNotification("test", "failure", 1, "BTC"));
        while(requests.Count == 0)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Equal(1, requests.Count);
        Assert.Contains(logs.Target.Logs, entry =>
            entry.Contains("NotificationService.LogGuarded"));
        Assert.Equal(0, service.PendingFormatting);
    }

    [Fact]
    public async Task DisabledEventsDoNotDisplaceUncertainPayout()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig { Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" } },
        };
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus)
        {
            MaxPendingOrdinaryNotifications = 2,
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        service.BeforePaymentFormat = _ =>
        {
            entered.Set();
            release.Wait(deadline.Token);
        };

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        try
        {
            bus.SendMessage(new PaymentNotification("test", "failure", 1, "BTC")
            {
                Outcome = PaymentNotificationOutcome.Uncertain,
            });
            await Task.Run(() => entered.Wait(deadline.Token), deadline.Token);
            for(var index = 0; index < 5; index++)
            {
                bus.SendMessage(new PaymentNotification("test", null, 1, "BTC"));
                bus.SendMessage(new BlockFoundNotification { PoolId = "test", BlockHeight = (ulong) index });
            }
            bus.SendMessage(new PaymentNotification("test", "failure", 1, "BTC")
            {
                Outcome = PaymentNotificationOutcome.Uncertain,
            });

            Assert.Equal(2, service.PendingFormatting);
            Assert.Equal(0, service.DroppedFormatting);
        }
        finally
        {
            release.Set();
        }

        while(requests.Count < 2)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task RoutineQueueTrafficReservesCapacityForPayoutIncident()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true, NotifyPaymentSuccess = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig { Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" } },
        };
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Warn);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger)
        {
            MaxPendingOrdinaryNotifications = 2,
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        service.BeforePaymentFormat = _ =>
        {
            entered.Set();
            release.Wait(deadline.Token);
        };

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        try
        {
            bus.SendMessage(new PaymentNotification("test", null, 1, "BTC"));
            await Task.Run(() => entered.Wait(deadline.Token), deadline.Token);
            bus.SendMessage(new AdminNotification("routine", "message"));
            bus.SendMessage(new PaymentNotification("test", "failure", 1, "BTC")
            {
                Outcome = PaymentNotificationOutcome.Uncertain,
            });

            Assert.Equal(2, service.PendingFormatting);
            Assert.Equal(1, service.DroppedFormatting);
            Assert.Contains(logs.Target.Logs, entry =>
                entry.Contains("formatting queue full; routine alert omitted"));
        }
        finally
        {
            release.Set();
        }

        while(requests.Count < 2)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("Direct SOLO block submission outcome is uncertain", "test")]
    [InlineData("Share Recorder Policy Fallback", null)]
    public async Task ImportantAdminAlertsSurviveRoutineSaturationAndReportOmission(
        string subject, string poolId)
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true, NotifyPaymentSuccess = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig { Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" } },
        };
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Warn);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger)
        {
            MaxPendingOrdinaryNotifications = 2,
        };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        service.BeforePaymentFormat = _ =>
        {
            entered.Set();
            release.Wait(deadline.Token);
        };

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        try
        {
            bus.SendMessage(new PaymentNotification("test", null, 1, "BTC"));
            await Task.Run(() => entered.Wait(deadline.Token), deadline.Token);
            bus.SendMessage(new AdminNotification("routine", "message"));
            for(var index = 0; index < 2; index++)
                bus.SendMessage(new AdminNotification(subject, "Check incident evidence",
                    AdminNotificationImportance.FinancialOrSecurity, poolId));

            Assert.Equal(2, service.PendingFormatting);
            Assert.Equal(2, service.DroppedFormatting);
            Assert.Equal(2, logs.Target.Logs.Count);
            Assert.Contains(logs.Target.Logs, entry =>
                entry.Contains("formatting queue full; routine alert omitted"));
            Assert.Contains(logs.Target.Logs, entry =>
                entry.Contains("financial or security administrative alert not queued") &&
                entry.Contains(poolId == null ? "for the cluster" : $"for pool {poolId}"));
        }
        finally
        {
            release.Set();
        }

        while(requests.Count < 2)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task OversizedPayoutUsesSmallOperatorSummary()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig { Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" } },
        };
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Warn);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        bus.SendMessage(new PaymentNotification("test", "private failure", 1, "BTC")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            Reconciliation = new Miningcore.Payments.PayoutReconciliation
            {
                Accepted = new[]
                {
                    new Miningcore.Payments.PayoutReconciliationEntry { Amount = 0.25m },
                },
                Failed = new[]
                {
                    new Miningcore.Payments.PayoutReconciliationEntry { Amount = 0.5m },
                },
                Uncertain = new[]
                {
                    new Miningcore.Payments.PayoutReconciliationEntry
                    {
                        Address = new string('x', 512 * 1024 + 1),
                        Amount = 0.75m,
                    },
                },
                NotAttempted = new[]
                {
                    new Miningcore.Payments.PayoutReconciliationEntry { Amount = 1m },
                },
            },
        });
        while(requests.Count == 0)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Contains(logs.Target.Logs, entry =>
            entry.Contains("reduced to an operator summary"));
        var body = Uri.UnescapeDataString(requests.LastBody);
        Assert.Contains("Per-recipient detail was omitted", body);
        Assert.Contains("Accepted/persisted: 1 recipient(s), 0.25 BTC", body);
        Assert.Contains("Failed: 1 recipient(s), 0.5 BTC", body);
        Assert.Contains("Uncertain: 1 recipient(s), 0.75 BTC", body);
        Assert.Contains("Not attempted: 1 recipient(s), 1 BTC", body);
        Assert.Contains("Reconcile wallet history before retrying", body);
        Assert.DoesNotContain(new string('x', 100), body);
    }

    [Fact]
    public async Task LargeReconciliationKeepsAggregateSummary()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        config.Pools = new[]
        {
            new PoolConfig { Id = "test", Template = new BitcoinTemplate { Symbol = "BTC" } },
        };
        var bus = new MessageBus();
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entries = Enumerable.Range(0, 10_000)
            .Select(_ => new Miningcore.Payments.PayoutReconciliationEntry
            {
                Amount = 0.01m,
            }).ToArray();

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        bus.SendMessage(new PaymentNotification("test", "private failure", 100, "BTC")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            Reconciliation = new Miningcore.Payments.PayoutReconciliation
            {
                Uncertain = entries,
            },
        });
        while(requests.Count == 0)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Contains("Uncertain: 10000 recipient(s), 100 BTC",
            Uri.UnescapeDataString(requests.LastBody));
    }

    [Fact]
    public async Task BoundedEmailQueueReportsRoutineOverflow()
    {
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) smtp.LocalEndpoint).Port;
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
            Email = email,
        });
        var bus = new MessageBus();
        using var logs = new TestLogs(LogLevel.Warn);
        using var service = new NotificationService(config, null, bus, logs.Logger)
        {
            MaxPendingOrdinaryNotifications = 3,
            OrdinaryTransportTimeout = TimeSpan.FromSeconds(5),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = smtp.AcceptTcpClientAsync(deadline.Token).AsTask();

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        bus.SendMessage(new AdminNotification("first", "message"));
        using var peer = await accepted.WaitAsync(deadline.Token);
        bus.SendMessage(new AdminNotification("second", "message"));
        while(service.PendingEmail < 2)
            await Task.Delay(10, deadline.Token);
        bus.SendMessage(new AdminNotification("third", "message"));
        while(service.DroppedEmail == 0)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Equal(1, service.DroppedEmail);
        Assert.Contains(logs.Target.Logs, entry =>
            entry.Contains("email queue full; routine alert omitted"));
    }

    [Fact]
    public async Task StalledOrdinaryEmailTimesOutAndNextPushIsDelivered()
    {
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) smtp.LocalEndpoint).Port;
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
        var admin = new Subject<AdminNotification>();
        var bus = Substitute.For<IMessageBus>();
        bus.Listen<AdminNotification>().Returns(admin);
        bus.Listen<BlockFoundNotification>().Returns(new Subject<BlockFoundNotification>());
        bus.Listen<PaymentNotification>().Returns(new Subject<PaymentNotification>());
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger)
        {
            OrdinaryTransportTimeout = TimeSpan.FromMilliseconds(250),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = smtp.AcceptTcpClientAsync(deadline.Token).AsTask();

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        admin.OnNext(new AdminNotification("first", "first incident"));
        using var peer = await accepted.WaitAsync(deadline.Token);
        while(requests.Count == 0)
            await Task.Delay(10, deadline.Token);
        admin.OnNext(new AdminNotification("second", "second incident"));
        while(requests.Count < 2)
            await Task.Delay(10, deadline.Token);
        while(!logs.Target.Logs.Any(entry =>
                  entry.Contains("Ordinary email delivery exceeded")))
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Equal(2, requests.Count);
        Assert.Contains("Ordinary email delivery exceeded",
            string.Join("\n", logs.Target.Logs));
        Assert.All(logs.Target.Logs, entry =>
            Assert.Contains("Ordinary email delivery exceeded", entry));
        Assert.DoesNotContain(logs.Target.Logs, entry =>
            entry.Contains("Ordinary Pushover delivery exceeded"));
    }

    [Fact]
    public async Task LaterOrdinaryPushDoesNotWaitForEarlierEmailTimeout()
    {
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) smtp.LocalEndpoint).Port;
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
        var admin = new Subject<AdminNotification>();
        var bus = Substitute.For<IMessageBus>();
        bus.Listen<AdminNotification>().Returns(admin);
        bus.Listen<BlockFoundNotification>().Returns(new Subject<BlockFoundNotification>());
        bus.Listen<PaymentNotification>().Returns(new Subject<PaymentNotification>());
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger)
        {
            OrdinaryTransportTimeout = TimeSpan.FromSeconds(5),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var accepted = smtp.AcceptTcpClientAsync(deadline.Token).AsTask();

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        admin.OnNext(new AdminNotification("first", "first incident"));
        using var peer = await accepted.WaitAsync(deadline.Token);
        while(requests.Count < 1)
            await Task.Delay(10, deadline.Token);
        admin.OnNext(new AdminNotification("second", "second incident"));
        while(requests.Count < 2)
            await Task.Delay(10, deadline.Token);
        using var sent = JsonDocument.Parse(requests.LastBody);
        Assert.Equal("second", sent.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(logs.Target.Logs, entry =>
            entry.Contains("Ordinary email delivery exceeded"));
        await service.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task HostShutdownCancelsOrdinaryEmailWithoutErrorLog()
    {
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var email = ValidEmail();
        email.Port = ((IPEndPoint) smtp.LocalEndpoint).Port;
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications
            {
                Enabled = true, EmailAddress = "operator@example.invalid",
            },
            Email = email,
        });
        var admin = new Subject<AdminNotification>();
        var bus = Substitute.For<IMessageBus>();
        bus.Listen<AdminNotification>().Returns(admin);
        bus.Listen<BlockFoundNotification>().Returns(new Subject<BlockFoundNotification>());
        bus.Listen<PaymentNotification>().Returns(new Subject<PaymentNotification>());
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config, null, bus, logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = smtp.AcceptTcpClientAsync(deadline.Token).AsTask();

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        admin.OnNext(new AdminNotification("incident", "failure"));
        using var peer = await accepted.WaitAsync(deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.True(logs.Target.Logs.Count == 0,
            string.Join("\n", logs.Target.Logs));
    }

    [Fact]
    public async Task OrdinaryPushoverFailureLogNamesTheTransport()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var admin = new Subject<AdminNotification>();
        var bus = Substitute.For<IMessageBus>();
        bus.Listen<AdminNotification>().Returns(admin);
        bus.Listen<BlockFoundNotification>().Returns(new Subject<BlockFoundNotification>());
        bus.Listen<PaymentNotification>().Returns(new Subject<PaymentNotification>());
        var requests = new RecordingPushoverHandler(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var logs = new TestLogs(LogLevel.Error);
        using var service = new NotificationService(config,
            new PushoverClient(config, factory), bus, logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        admin.OnNext(new AdminNotification("incident", "failure"));
        while(logs.Target.Logs.Count == 0)
            await Task.Delay(10, deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Contains("Ordinary Pushover delivery failed",
            string.Join("\n", logs.Target.Logs));
        Assert.Single(logs.Target.Logs);
    }

    [Fact]
    public async Task UnhostedCriticalSenderAttemptsPushDespiteEmailFailure()
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
    public async Task MultiCandidateCriticalPushKeepsFatalStateWithinLimit()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());
        var status = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        var fatalPath = "/protected/recovery.fatal" + new string('f', 450);
        fatalState.FatalStateFilename.Returns(fatalPath);
        var handler = new CandidatePersistenceFailureHandler(
            new MiningFailStopCoordinator(status, lifetime),
            new Lazy<ICriticalNotificationSender>(() => sender), fatalState);
        var candidates = Enumerable.Range(0, 10)
            .Select(index => new Miningcore.Blockchain.Share
            {
                PoolId = "btc", Miner = "bc1q" + new string('m', 75),
                BlockHeight = index + 1,
                BlockType = "bitcoin", BlockHash = new string('a', 64),
            }).ToArray();

        await handler.StopClusterAsync(candidates, new IOException("database"),
            new IOException("journal"), false);

        using var document = JsonDocument.Parse(requests.LastBody);
        var message = document.RootElement.GetProperty("message").GetString();
        Assert.True(message.EnumerateRunes().Count() <= 1024);
        Assert.Contains("Fatal state: /protected/recovery.fatal", message);
        Assert.Contains(fatalPath, message);
        Assert.Contains("details omitted", message);
        Assert.EndsWith("Preserve PostgreSQL and recovery evidence; see Miningcore logs for full details.",
            message);
        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            status.ExitCode);
    }

    [Fact]
    public async Task RecoveryHandlerKeepsFatalOutcomeWhenBothTransportsFail()
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
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());
        var status = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("/protected/recovery.fatal");
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(status, lifetime),
            new Lazy<ICriticalNotificationSender>(() => sender), fatalState);

        await handler.StopClusterAsync(new[]
        {
            new Miningcore.Blockchain.Share { PoolId = "btc", Miner = "miner" },
        }, "/protected/recovery.txt", new IOException("database failure"),
            new IOException("journal failure"));

        Assert.Equal(1, requests.Count);
        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            status.ExitCode);
        lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task OversizedRecoveryAlertKeepsFullOperatorInstruction()
    {
        var config = Config(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Pushover = new PushoverConfig
            {
                Enabled = true, User = "test-user", Token = "test-token",
            },
        });
        var requests = new RecordingPushoverHandler();
        using var http = new HttpClient(requests);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
        using var sender = new NotificationService(config,
            new PushoverClient(config, factory), Substitute.For<IMessageBus>());
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        var fatalPath = "/protected/recovery.fatal" + new string('f', 450);
        fatalState.FatalStateFilename.Returns(fatalPath);
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(new ProcessStatus(),
                Substitute.For<IHostApplicationLifetime>()),
            new Lazy<ICriticalNotificationSender>(() => sender), fatalState);
        var shares = Enumerable.Range(0, 12)
            .Select(index => new Miningcore.Blockchain.Share
            {
                PoolId = $"pool-{index}-" + new string('p', 70),
                Miner = "miner",
            }).ToArray();

        await handler.StopClusterAsync(shares, "/protected/recovery.txt",
            new IOException("database"), new IOException("journal"));

        using var document = JsonDocument.Parse(requests.LastBody);
        var message = document.RootElement.GetProperty("message").GetString();
        Assert.True(message.EnumerateRunes().Count() <= 1024);
        Assert.Contains("details omitted", message);
        Assert.Contains(fatalPath, message);
        Assert.Contains(ShareRecoveryFatalState.OperatorAcknowledgementInstruction,
            message);
    }

    [Fact]
    public async Task DeprecatedTopLevelEnabledWarnsWhenAdminIsInactive()
    {
        var config = Config(new NotificationsConfig
        {
            Enabled = true,
            Admin = new AdminNotifications { Enabled = false },
        });
        using var logs = new TestLogs(LogLevel.Warn);
        using var service = new NotificationService(config, null,
            Substitute.For<IMessageBus>(), logs.Logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        await service.StopAsync(deadline.Token);

        Assert.Contains(logs.Target.Logs, entry =>
            entry.Contains("notifications.enabled is deprecated and ignored"));
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

    private static EmailSenderConfig ValidEmail()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return new EmailSenderConfig
        {
            Host = "127.0.0.1", Port = closedPort,
            TlsMode = SmtpTlsMode.StartTls,
            User = "test-user", Password = "test-password",
            FromName = "Operator", FromAddress = "pool@example.invalid",
        };
    }

    private sealed class RecordingPushoverHandler(
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private int count;
        private string lastBody;
        public int Count => Volatile.Read(ref count);
        public string LastBody => Volatile.Read(ref lastBody);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Volatile.Write(ref lastBody,
                await request.Content.ReadAsStringAsync(cancellationToken));
            Interlocked.Increment(ref count);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"status\":1}"),
            };
        }
    }

    private sealed class TestLogs : IDisposable
    {
        private readonly LogFactory factory = new();
        public MemoryTarget Target { get; } = new()
        {
            Layout = "${level}|${message}|${exception:format=message}",
        };
        public ILogger Logger { get; }

        public TestLogs(LogLevel minimum)
        {
            var configuration = new LoggingConfiguration(factory);
            configuration.AddRule(minimum, LogLevel.Fatal, Target);
            factory.Configuration = configuration;
            Logger = factory.GetLogger("NotificationServiceTests");
        }

        public void Dispose() => factory.Dispose();
    }
}
