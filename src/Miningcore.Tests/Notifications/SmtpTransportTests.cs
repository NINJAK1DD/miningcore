using System;
using System.Collections.Generic;
using System.IO;
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
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Notifications;
using NSubstitute;
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
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void CertificateErrorsAreRejected(SslPolicyErrors errors)
    {
        Assert.False(SmtpTransportPolicy.CertificateIsValid(errors));
        Assert.True(SmtpTransportPolicy.CertificateIsValid(SslPolicyErrors.None));
    }

    [Fact]
    public void StartupValidationRequiresExplicitTlsMode()
    {
        var config = Config(null, 587);
        var result = new NotificationsConfigValidator().Validate(new NotificationsConfig
        {
            Enabled = true,
            Email = config,
        });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("tlsMode"));
    }

    [Fact]
    public async Task CanceledSendDoesNotConnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var config = Config("startTls", ((IPEndPoint) listener.LocalEndpoint).Port);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(config)
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
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(config)
            .SendEmailAsync("recipient@example.com", "subject", "body", timeout.Token));
        await server;
        Assert.StartsWith("EHLO", commands[0]);
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
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(config)
            .SendEmailAsync("recipient@example.com", "subject", "body", timeout.Token));
        await server;
        if(mode == "startTls")
        {
            Assert.StartsWith("EHLO", commands[0]);
            Assert.Equal("STARTTLS", commands[1]);
        }
        Assert.DoesNotContain(commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    private static EmailSenderConfig Config(string mode, int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        TlsMode = mode,
        User = "user",
        Password = "password",
        FromAddress = "sender@example.com",
        FromName = "Sender",
    };

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
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
