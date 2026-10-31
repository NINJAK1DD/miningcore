using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Security;
using MailKit.Net.Smtp;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Notifications;
using Miningcore.Mining;
using NSubstitute;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Notifications;

public class SmtpTransportTests
{
    [Theory]
    [InlineData("startTls", 587, SecureSocketOptions.StartTls)]
    [InlineData("startTls", 25, SecureSocketOptions.StartTls)]
    [InlineData("implicitTls", 465, SecureSocketOptions.SslOnConnect)]
    [InlineData("localPlaintext", 2525, SecureSocketOptions.None)]
    public void ExplicitModesSelectTransport(string mode, int port, SecureSocketOptions expected)
    {
        var config = Config(mode, port);
        if(mode == "localPlaintext")
            config.User = config.Password = null;
        Assert.Equal(expected, SmtpTransportPolicy.Resolve(config));
    }

    [Theory]
    [InlineData(null, "127.0.0.1", 587, "user", "password")]
    [InlineData("auto", "127.0.0.1", 587, "user", "password")]
    [InlineData("startTls", "127.0.0.1", 465, "user", "password")]
    [InlineData("implicitTls", "127.0.0.1", 587, "user", "password")]
    [InlineData("localPlaintext", "smtp.example.com", 2525, null, null)]
    [InlineData("localPlaintext", "localhost", 2525, null, null)]
    [InlineData("localPlaintext", "127.0.0.1", 2525, "user", "password")]
    [InlineData("startTls", "127.0.0.1", 587, "user", null)]
    [InlineData("startTls", "127.0.0.1", 0, "user", "password")]
    [InlineData("startTls", "127.0.0.1", 65536, "user", "password")]
    [InlineData("implicitTls", "127.0.0.1", 25, "user", "password")]
    [InlineData("localPlaintext", "127.0.0.1", 465, null, null)]
    public void UnsafeConfigurationIsRejected(string mode, string host, int port,
        string user, string password)
    {
        var config = Config(mode, port);
        config.Host = host;
        config.User = user;
        config.Password = password;
        Assert.Throws<InvalidOperationException>(() => SmtpTransportPolicy.Resolve(config));
    }

    [Theory]
    [InlineData("StartTls")]
    [InlineData("starttls")]
    [InlineData(" startTls")]
    [InlineData("startTls ")]
    [InlineData("auto")]
    [InlineData("1")]
    [InlineData("startTls,implicitTls")]
    public void ModeConverterRejectsAliases(string mode)
    {
        Assert.Throws<JsonSerializationException>(() =>
            JsonConvert.DeserializeObject<EmailSenderConfig>(
                "{\"tlsMode\":\"" + mode + "\"}"));
    }

    [Fact]
    public void ModeConverterRejectsInteger()
    {
        Assert.Throws<JsonSerializationException>(() =>
            JsonConvert.DeserializeObject<EmailSenderConfig>("{\"tlsMode\":1}"));
    }

    [Fact]
    public void ModeSchemaContainsOnlyDocumentedValues()
    {
        var values = (JArray) Miningcore.Program.GenerateJsonConfigSchemaDocument()
            .SelectToken("definitions.EmailSenderConfig.properties.tlsMode.enum");
        Assert.Equal(new[] { null, "startTls", "implicitTls", "localPlaintext" },
            values.Select(value => value.Type == JTokenType.Null ? null : value.Value<string>()));
    }

    [Fact]
    public void InvalidEndpointHasOneConfigurationError()
    {
        var config = Config("startTls", 0);
        config.Host = null;
        var result = new EmailSenderConfigValidator().Validate(config);
        Assert.Single(result.Errors);
        Assert.DoesNotContain("user", result.Errors[0].ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void CertificateErrorsAreRejected(SslPolicyErrors errors)
    {
        Assert.False(SmtpTransportPolicy.CertificateIsValid(errors));
        Assert.True(SmtpTransportPolicy.CertificateIsValid(SslPolicyErrors.None));
    }

    [Fact]
    public void CertificateValidationOverrideIsNotPublic()
    {
        Assert.DoesNotContain(typeof(NotificationService).GetMethods(), method =>
            method.Name == nameof(NotificationService.SendEmailAsync) &&
            method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(RemoteCertificateValidationCallback)));
    }

    [Fact]
    public void StartupValidationRequiresExplicitTlsMode()
    {
        var config = Config(null, 587);
        var result = new NotificationsConfigValidator().Validate(new NotificationsConfig
        {
            Admin = new AdminNotifications { Enabled = true },
            Email = config,
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("tlsMode"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OnlyAdministrativeEmailActivatesSmtpValidation(bool notificationsEnabled,
        bool adminEnabled)
    {
        var result = new NotificationsConfigValidator().Validate(new NotificationsConfig
        {
            Enabled = notificationsEnabled,
            Admin = new AdminNotifications { Enabled = adminEnabled },
            Email = Config(null, 587),
        });
        Assert.Equal(!adminEnabled, result.IsValid);

        var missingEmail = new NotificationsConfigValidator().Validate(new NotificationsConfig
        {
            Enabled = notificationsEnabled,
            Admin = new AdminNotifications { Enabled = adminEnabled },
        });
        Assert.True(missingEmail.IsValid);
    }

    [Fact]
    public void LocalPlaintextConfigurationPassesNormalStartupValidation()
    {
        var config = ExampleConfig();
        config.Notifications.Admin.Enabled = true;
        config.Notifications.Email.TlsMode = SmtpTlsMode.LocalPlaintext;
        config.Notifications.Email.Host = "127.0.0.1";
        config.Notifications.Email.Port = 2525;
        config.Notifications.Email.User = null;
        config.Notifications.Email.Password = null;

        Miningcore.Program.ValidateConfig(config, false);
    }

    [Fact]
    public void EnabledNotificationsWithoutAdminEmailRemainValid()
    {
        var config = ExampleConfig();
        config.Notifications.Enabled = true;
        config.Notifications.Admin.Enabled = false;
        config.Notifications.Email = null;

        Miningcore.Program.ValidateConfig(config, false);
    }

    [Fact]
    public void EnabledAdminEmailStillRequiresSenderConfiguration()
    {
        var config = ExampleConfig();
        config.Notifications.Admin.Enabled = true;
        config.Notifications.Email = null;

        Assert.Throws<PoolStartupException>(() =>
            Miningcore.Program.ValidateConfig(config, false));
    }

    [Fact]
    public async Task HostedSenderRejectsInvalidSmtpBeforeSubscribing()
    {
        var messageBus = Substitute.For<IMessageBus>();
        using var sender = new NotificationService(new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            Notifications = new NotificationsConfig
            {
                Admin = new AdminNotifications { Enabled = true },
                Email = Config(null, 587),
            },
        }, null, messageBus);

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            sender.StartAsync(CancellationToken.None));
        Assert.Contains("tlsMode", error.Message);
        messageBus.DidNotReceive().Listen<Miningcore.Notifications.Messages.AdminNotification>();
    }

    [Fact]
    public async Task CanceledSendDoesNotConnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var config = Config("startTls", ((IPEndPoint) listener.LocalEndpoint).Port);
        using var sender = Sender(config);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender
            .SendEmailAsync("recipient@example.com", "subject", "body", canceled.Token));
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task ExplicitLoopbackPlaintextRelaySendsWithoutAuthentication()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
                { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 loopback ESMTP");
            var inData = false;
            while(true)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                if(line == null)
                    break;
                if(inData)
                {
                    if(line == ".")
                    {
                        inData = false;
                        await writer.WriteLineAsync("250 accepted");
                    }
                    continue;
                }

                commands.Add(line);
                if(line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250 loopback");
                else if(line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250 accepted");
                else if(line == "DATA")
                {
                    inData = true;
                    await writer.WriteLineAsync("354 send message");
                }
                else if(line == "QUIT")
                {
                    await writer.WriteLineAsync("221 goodbye");
                    break;
                }
                else
                    throw new InvalidOperationException("Unexpected SMTP command");
            }
        }, timeout.Token);

        var config = Config("localPlaintext", ((IPEndPoint) listener.LocalEndpoint).Port);
        config.User = config.Password = null;
        using var sender = Sender(config);
        await sender.SendEmailAsync("recipient@example.com", "subject", "body", timeout.Token);
        await server;
        Assert.Contains(commands, command => command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingStartTlsFailsBeforeAuthentication()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
                { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 loopback ESMTP");
            commands.Add(await reader.ReadLineAsync(timeout.Token));
            await writer.WriteLineAsync("250 loopback");
            var next = await reader.ReadLineAsync(timeout.Token);
            if(next != null)
                commands.Add(next);
        }, timeout.Token);

        var config = Config("startTls", ((IPEndPoint) listener.LocalEndpoint).Port);
        using var sender = Sender(config);
        await Assert.ThrowsAsync<NotSupportedException>(() => sender
            .SendEmailAsync("recipient@example.com", "subject", "body", timeout.Token));
        await server;
        Assert.StartsWith("EHLO", commands[0]);
        Assert.DoesNotContain(commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectedStartTlsFailsBeforeAuthentication()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
                { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 loopback ESMTP");
            commands.Add(await reader.ReadLineAsync(timeout.Token));
            await writer.WriteLineAsync("250-loopback");
            await writer.WriteLineAsync("250 STARTTLS");
            commands.Add(await reader.ReadLineAsync(timeout.Token));
            await writer.WriteLineAsync("454 TLS temporarily unavailable");
            var next = await reader.ReadLineAsync(timeout.Token);
            if(next != null)
                commands.Add(next);
        }, timeout.Token);

        var config = Config("startTls", ((IPEndPoint) listener.LocalEndpoint).Port);
        using var sender = Sender(config);
        await Assert.ThrowsAsync<SmtpCommandException>(() => sender
            .SendEmailAsync("recipient@example.com", "subject", "body", timeout.Token));
        await server;
        Assert.StartsWith("EHLO", commands[0]);
        Assert.Equal("STARTTLS", commands[1]);
        Assert.DoesNotContain(commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("startTls")]
    [InlineData("implicitTls")]
    public async Task UntrustedCertificateFailsBeforeAuthentication(string mode)
    {
        using var certificate = CreateSelfSignedCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            if(mode == "startTls")
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
                    { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 loopback ESMTP");
                commands.Add(await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("250-loopback");
                await writer.WriteLineAsync("250 STARTTLS");
                commands.Add(await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("220 Ready to start TLS");
            }

            using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, timeout.Token);
            }
            catch(System.Security.Authentication.AuthenticationException)
            {
                // The client rejects the test certificate and closes the handshake.
            }
            catch(IOException)
            {
                // Platform TLS stacks may report the same close as an I/O failure.
            }
        }, timeout.Token);

        var config = Config(mode, ((IPEndPoint) listener.LocalEndpoint).Port);
        using var sender = Sender(config);
        await Assert.ThrowsAsync<SslHandshakeException>(() => sender
            .SendEmailAsync("recipient@example.com", "subject", "body", timeout.Token));
        await server;
        if(mode == "startTls")
        {
            Assert.StartsWith("EHLO", commands[0]);
            Assert.Equal("STARTTLS", commands[1]);
        }
        Assert.DoesNotContain(commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("startTls")]
    [InlineData("implicitTls")]
    public async Task AuthenticatedDeliverySucceedsOverTrustedTls(string mode)
    {
        var (root, certificate) = CreateSignedCertificate();
        using(root)
        using(certificate)
        using(var listener = new TcpListener(IPAddress.Loopback, 0))
        using(var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            listener.Start();
            var beforeTls = new List<string>();
            var afterTls = new List<string>();
            var credentialsAccepted = false;
            var messageAccepted = false;
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = peer.GetStream();
                if(mode == "startTls")
                    await BeginStartTls(stream, beforeTls, timeout.Token);

                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, timeout.Token);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true)
                    { NewLine = "\r\n", AutoFlush = true };
                if(mode == "implicitTls")
                    await writer.WriteLineAsync("220 loopback ESMTP");
                var inData = false;
                while(true)
                {
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if(line == null)
                        break;
                    if(inData)
                    {
                        if(line == ".")
                        {
                            inData = false;
                            messageAccepted = true;
                            await writer.WriteLineAsync("250 accepted");
                        }
                        continue;
                    }

                    afterTls.Add(line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase)
                        ? "AUTH PLAIN" : line);
                    if(line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-loopback");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                    }
                    else if(line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = line.Length > "AUTH PLAIN".Length
                            ? line[("AUTH PLAIN".Length + 1)..]
                            : null;
                        if(payload == null)
                        {
                            await writer.WriteLineAsync("334 ");
                            payload = await reader.ReadLineAsync(timeout.Token);
                        }
                        credentialsAccepted = Encoding.UTF8.GetString(
                            Convert.FromBase64String(payload)) == "\0user\0password";
                        if(!credentialsAccepted)
                            throw new InvalidOperationException("Unexpected test credentials");
                        await writer.WriteLineAsync("235 authenticated");
                    }
                    else if(line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync("250 accepted");
                    else if(line == "DATA")
                    {
                        inData = true;
                        await writer.WriteLineAsync("354 send message");
                    }
                    else if(line == "QUIT")
                    {
                        await writer.WriteLineAsync("221 goodbye");
                        break;
                    }
                    else
                        throw new InvalidOperationException("Unexpected SMTP command");
                }
            }, timeout.Token);

            var config = Config(mode, ((IPEndPoint) listener.LocalEndpoint).Port);
            using var sender = Sender(config);
            try
            {
                await sender.SendEmailAsync("recipient@example.com", "subject", "body",
                    timeout.Token, TrustOnly(root));
            }
            catch(Exception clientError)
            {
                try { await server; }
                catch(Exception serverError)
                {
                    throw new AggregateException(clientError, serverError);
                }
                throw;
            }
            await server;
            Assert.DoesNotContain(beforeTls, line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(afterTls, line => line == "AUTH PLAIN");
            Assert.Contains(afterTls, line => line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase));
            Assert.True(credentialsAccepted);
            Assert.True(messageAccepted);
        }
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("expired")]
    public async Task InvalidTrustedCertificateFailsBeforeAuthentication(string failure)
    {
        // Expiry is checked by the isolated test trust callback; the hostname case
        // exercises MailKit's production name check against the configured host.
        var (root, certificate) = CreateSignedCertificate(
            expired: failure == "expired", wrongHost: failure == "hostname");
        using(root)
        using(certificate)
        using(var listener = new TcpListener(IPAddress.Loopback, 0))
        using(var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            listener.Start();
            var beforeTls = new List<string>();
            var afterTls = new List<string>();
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = peer.GetStream();
                await BeginStartTls(stream, beforeTls, timeout.Token);
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, timeout.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if(line != null)
                        afterTls.Add(line);
                }
                catch(System.Security.Authentication.AuthenticationException) { }
                catch(IOException) { }
            }, timeout.Token);

            var config = Config("startTls", ((IPEndPoint) listener.LocalEndpoint).Port);
            using var sender = Sender(config);
            await Assert.ThrowsAsync<SslHandshakeException>(() => sender.SendEmailAsync(
                "recipient@example.com", "subject", "body", timeout.Token, TrustOnly(root)));
            await server;
            Assert.DoesNotContain(beforeTls, line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(afterTls, line => line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task BeginStartTls(Stream stream, List<string> commands,
        CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 loopback ESMTP");
        commands.Add(await reader.ReadLineAsync(ct));
        await writer.WriteLineAsync("250-loopback");
        await writer.WriteLineAsync("250 STARTTLS");
        commands.Add(await reader.ReadLineAsync(ct));
        Assert.StartsWith("EHLO", commands[0]);
        Assert.Equal("STARTTLS", commands[1]);
        await writer.WriteLineAsync("220 Ready to start TLS");
    }

    private static RemoteCertificateValidationCallback TrustOnly(X509Certificate2 root) =>
        (_, certificate, _, errors) =>
        {
            if(certificate == null ||
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(
                new Oid("1.3.6.1.5.5.7.3.1"));
            if(certificate is X509Certificate2 server)
                return chain.Build(server);
            using var converted = new X509Certificate2(certificate);
            return chain.Build(converted);
        };

    private static (X509Certificate2 Root, X509Certificate2 Server) CreateSignedCertificate(
        bool expired = false, bool wrongHost = false)
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest($"CN=SMTP test root {Guid.NewGuid():N}", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            rootRequest.PublicKey, false));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(30));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        if(wrongHost)
            names.AddDnsName("wrong.example");
        else
        {
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
        }
        serverRequest.CertificateExtensions.Add(names.Build());
        var notBefore = DateTimeOffset.UtcNow.AddDays(expired ? -10 : -1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1);
        using var issued = serverRequest.Create(root, notBefore, notAfter,
            RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(serverKey);
        // Schannel cannot use the ephemeral private key produced by CopyWithPrivateKey.
        // Reimport the test-only certificate without EphemeralKeySet for Windows CI.
        var server = X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12, "smtp-test"), "smtp-test",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        return (root, server);
    }

    private static EmailSenderConfig Config(string mode, int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        TlsMode = mode switch
        {
            "startTls" => SmtpTlsMode.StartTls,
            "implicitTls" => SmtpTlsMode.ImplicitTls,
            "localPlaintext" => SmtpTlsMode.LocalPlaintext,
            _ => null,
        },
        User = "user",
        Password = "password",
        FromAddress = "sender@example.com",
        FromName = "Sender",
    };

    private static ClusterConfig ExampleConfig() =>
        JsonConvert.DeserializeObject<ClusterConfig>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "config.example.json")));

    private static NotificationService Sender(EmailSenderConfig email) =>
        new(new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            Notifications = new NotificationsConfig { Email = email },
        }, null, Substitute.For<IMessageBus>());

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
