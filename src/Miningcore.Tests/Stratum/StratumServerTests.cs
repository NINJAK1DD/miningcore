using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Banning;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PrepublishedMergedShare_WaitsForPropagatedJournalAdmissionBeforeResponse()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            lifetime);
        var builder = new ContainerBuilder();
        builder.RegisterInstance<IMiningFailStopCoordinator>(coordinator);
        using var container = builder.Build();
        var server = new TestStratumServer(container, new MessageBus(coordinator));
        var journalCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var share = new Share { StatisticalRecordEmitted = true };
        share.SetPersistenceAdmission(journalCommit.Task);
        var acknowledged = false;

        var admission = server.AdmitAsync(share, () =>
        {
            acknowledged = true;
            return Task.CompletedTask;
        }, false);

        await Task.Delay(25);
        Assert.False(admission.IsCompleted);
        Assert.False(acknowledged);

        journalCommit.TrySetResult();
        await admission.WaitAsync(TestTimeout);
        Assert.True(acknowledged);
    }

    [Fact]
    public async Task PrepublishedMergedShare_JournalFailureDoesNotQueuePositiveResponse()
    {
        var processStatus = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(processStatus, lifetime);
        var builder = new ContainerBuilder();
        builder.RegisterInstance<IMiningFailStopCoordinator>(coordinator);
        using var container = builder.Build();
        var server = new TestStratumServer(container, new MessageBus(coordinator));
        var journalCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var share = new Share { StatisticalRecordEmitted = true };
        share.SetPersistenceAdmission(journalCommit.Task);
        var acknowledged = false;
        var admission = server.AdmitAsync(share, () =>
        {
            acknowledged = true;
            return Task.CompletedTask;
        }, false);

        Assert.True(coordinator.BeginFailStop(
            ProcessExitCodes.UnreconciledShareDurabilityLoss));
        journalCommit.TrySetException(new IOException(
            "injected merged-mining emergency-journal failure"));

        var error = await Assert.ThrowsAsync<IOException>(() => admission);
        Assert.Contains("emergency-journal failure", error.Message);
        Assert.False(acknowledged);
        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        lifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsIdleListenerPromptly()
    {
        var server = new TestStratumServer();
        using var cts = new CancellationTokenSource();
        var runTask = server.RunListenerAsync(cts.Token);

        cts.Cancel();

        // Cancellation disposes the listening socket synchronously, but completion of the
        // resulting AcceptAsync continuation still needs a thread-pool turn. Native hashing
        // tests can briefly saturate constrained CI runners, so retain a strict shutdown bound
        // without making scheduler latency look like a listener leak.
        await runTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RunAsync_ShutdownWithConnectedMiner_AllowsImmediateExclusiveRestart()
    {
        var server = new TestStratumServer();
        using var cts = new CancellationTokenSource();
        var port = GetFreePort();
        var endpoint = new StratumEndpoint(
            new IPEndPoint(IPAddress.Loopback, port), new PoolEndpoint());
        var runTask = server.RunListenerAsync(cts.Token, endpoint);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port,
            CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await server.WaitForConnectionCountAsync(1, TestTimeout);

        cts.Cancel();
        await runTask.WaitAsync(TestTimeout);

        using var restarted = StratumServer.CreateBoundSocket(
            endpoint.IPEndPoint);
    }

    [Fact]
    public async Task RunAsync_BannedClientRejection_AllowsImmediateExclusiveRestart()
    {
        var banManager = Substitute.For<IBanManager>();
        banManager.IsBanned(Arg.Any<IPAddress>()).Returns(true);
        var server = new TestStratumServer();
        server.SetBanManager(banManager);
        using var cts = new CancellationTokenSource();
        var port = GetFreePort();
        var endpoint = new StratumEndpoint(
            new IPEndPoint(IPAddress.Loopback, port), new PoolEndpoint());
        var runTask = server.RunListenerAsync(cts.Token, endpoint);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port,
            CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await WaitForRejectedSocketAsync(client.Client);
        banManager.Received().IsBanned(Arg.Any<IPAddress>());

        cts.Cancel();
        await runTask.WaitAsync(TestTimeout);

        using var restarted = StratumServer.CreateBoundSocket(
            endpoint.IPEndPoint);
    }

    [Fact]
    public async Task RunAsync_PreDispatchFailure_AllowsImmediateExclusiveRestart()
    {
        var server = new TestStratumServer
        {
            ThrowOnConnect = true,
        };
        using var cts = new CancellationTokenSource();
        var port = GetFreePort();
        var endpoint = new StratumEndpoint(
            new IPEndPoint(IPAddress.Loopback, port), new PoolEndpoint());
        var runTask = server.RunListenerAsync(cts.Token, endpoint);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port,
            CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await WaitForRejectedSocketAsync(client.Client);
        await server.WaitForNoConnectionsAsync(TestTimeout);

        cts.Cancel();
        await runTask.WaitAsync(TestTimeout);

        using var restarted = StratumServer.CreateBoundSocket(
            endpoint.IPEndPoint);
    }

    [Fact]
    public async Task RunAsync_MalformedJsonClient_AllowsImmediateExclusiveRestart()
    {
        var server = new TestStratumServer();
        using var cts = new CancellationTokenSource();
        var port = GetFreePort();
        var endpoint = new StratumEndpoint(
            new IPEndPoint(IPAddress.Loopback, port), new PoolEndpoint());
        var runTask = server.RunListenerAsync(cts.Token, endpoint);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port,
            CancellationToken.None).AsTask().WaitAsync(TestTimeout);
        await server.WaitForConnectionCountAsync(1, TestTimeout);
        await using var stream = client.GetStream();
        var malformedRequest = StratumConnection.Encoding.GetBytes(
            "not-json\n");
        await stream.WriteAsync(malformedRequest);
        await stream.FlushAsync();
        await server.WaitForNoConnectionsAsync(TestTimeout);

        // Keep the client object alive while stopping the listener. The server-side protocol
        // rejection must already have made its accepted-socket close abortive.
        cts.Cancel();
        await runTask.WaitAsync(TestTimeout);

        using var restarted = StratumServer.CreateBoundSocket(
            endpoint.IPEndPoint);
    }

    [Fact]
    public async Task RunAsync_InvalidTlsHandshake_AllowsImmediateExclusiveRestart()
    {
        // Windows Schannel on the validation host cannot acquire credentials for an ephemeral
        // self-signed PFX. Linux CI exercises the TLS failure and exclusive-rebind contract.
        if(OperatingSystem.IsWindows())
            return;

        const string pfxPassword = "miningcore-test-password";
        var pfxFile = Path.Combine(Path.GetTempPath(),
            $"miningcore-tls-rejection-{Guid.NewGuid():N}.pfx");
        var endpointConfig = new PoolEndpoint
        {
            Difficulty = 1,
            Tls = true,
            TlsPfxFile = pfxFile,
            TlsPfxPassword = pfxPassword,
        };
        var server = new TestStratumServer();
        using var cts = new CancellationTokenSource();
        Task runTask = null;

        try
        {
            using var certificate = CreateServerCertificate();
            await File.WriteAllBytesAsync(pfxFile,
                certificate.Export(X509ContentType.Pfx, pfxPassword));

            var port = GetFreePort();
            var endpoint = new StratumEndpoint(
                new IPEndPoint(IPAddress.Loopback, port), endpointConfig);
            runTask = server.RunListenerAsync(cts.Token, endpoint);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port,
                CancellationToken.None).AsTask().WaitAsync(TestTimeout);
            await server.WaitForConnectionCountAsync(1, TestTimeout);
            await using var stream = client.GetStream();
            var invalidHandshake = StratumConnection.Encoding.GetBytes(
                "not-a-tls-client\n");
            await stream.WriteAsync(invalidHandshake);
            await stream.FlushAsync();
            await server.WaitForNoConnectionsAsync(TestTimeout);

            // Keep the peer alive until after the server has stopped so this proves the TLS
            // failure path, rather than client disposal, permits immediate exclusive rebinding.
            cts.Cancel();
            await runTask.WaitAsync(TestTimeout);
            runTask = null;

            using var restarted = StratumServer.CreateBoundSocket(
                endpoint.IPEndPoint);
        }
        finally
        {
            cts.Cancel();

            if(runTask != null)
                await runTask.WaitAsync(TestTimeout);

            server.RemoveCachedCertificate(pfxFile)?.Dispose();
            File.Delete(pfxFile);
        }
    }

    [Fact]
    public async Task RunAsync_ShutdownDrainsInFlightRequestHandler()
    {
        var server = new TestStratumServer();
        var requestEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestHandler = async (_, _, ct) =>
        {
            requestEntered.TrySetResult();
            await releaseRequest.Task;
            Assert.True(ct.IsCancellationRequested);
        };
        using var cts = new CancellationTokenSource();
        var port = GetFreePort();
        var runTask = server.RunListenerAsync(cts.Token, new StratumEndpoint(
            new IPEndPoint(IPAddress.Loopback, port), new PoolEndpoint()));

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None)
            .AsTask().WaitAsync(TestTimeout);
        await using var stream = client.GetStream();
        var request = StratumConnection.Encoding.GetBytes(
            "{\"id\":1,\"method\":\"mining.submit\",\"params\":[]}\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        await requestEntered.Task.WaitAsync(TestTimeout);

        cts.Cancel();
        await Task.Delay(50);
        Assert.False(runTask.IsCompleted);

        releaseRequest.TrySetResult();
        await runTask.WaitAsync(TestTimeout);
        await server.WaitForNoConnectionsAsync(TestTimeout);
    }

    [Fact]
    public async Task RunAsync_UnresponsiveRequestHandlerFailsStopWithinReservedBudget()
    {
        var processStatus = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(processStatus, lifetime);
        var builder = new ContainerBuilder();
        builder.RegisterInstance<IMiningFailStopCoordinator>(coordinator);
        using var container = builder.Build();
        var server = new TestStratumServer(container, new MessageBus(coordinator));
        server.SetConnectionDrainTimeout(TimeSpan.FromMilliseconds(100));
        var requestEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestHandler = (_, _, _) =>
        {
            requestEntered.TrySetResult();
            return neverCompletes.Task;
        };
        using var cts = new CancellationTokenSource();
        var port = GetFreePort();
        var runTask = server.RunListenerAsync(cts.Token, new StratumEndpoint(
            new IPEndPoint(IPAddress.Loopback, port), new PoolEndpoint()));

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None)
            .AsTask().WaitAsync(TestTimeout);
        await using var stream = client.GetStream();
        var request = StratumConnection.Encoding.GetBytes(
            "{\"id\":1,\"method\":\"mining.submit\",\"params\":[]}\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        await requestEntered.Task.WaitAsync(TestTimeout);

        cts.Cancel();
        await runTask.WaitAsync(TestTimeout);

        Assert.True(coordinator.IsFailStopRequested);
        Assert.Equal(ProcessExitCodes.GeneralFailure, processStatus.ExitCode);
        lifetime.Received(1).StopApplication();
        Assert.False(neverCompletes.Task.IsCompleted);
    }

    [Fact]
    public async Task RunAsync_WithPasswordProtectedPfx_CompletesTlsHandshake()
    {
        const string pfxPassword = "miningcore-test-password";
        var pfxFile = Path.Combine(Path.GetTempPath(), $"miningcore-tls-{Guid.NewGuid():N}.pfx");
        var endpoint = new PoolEndpoint
        {
            Difficulty = 1,
            Tls = true,
            TlsPfxFile = pfxFile,
            TlsPfxPassword = pfxPassword,
        };
        var server = new TestStratumServer();
        using var cts = new CancellationTokenSource();
        Task runTask = null;

        try
        {
            using var certificate = CreateServerCertificate();
            await File.WriteAllBytesAsync(pfxFile,
                certificate.Export(X509ContentType.Pfx, pfxPassword));

            var validation = await new PoolEndpointValidator().ValidateAsync(endpoint);
            Assert.True(validation.IsValid,
                string.Join(Environment.NewLine, validation.Errors.Select(x => x.ErrorMessage)));

            var port = GetFreePort();
            runTask = server.RunListenerAsync(cts.Token, new StratumEndpoint(
                new IPEndPoint(IPAddress.Loopback, port), endpoint));

            var cachedCertificate = server.GetCachedCertificate(pfxFile);
            Assert.NotNull(cachedCertificate);
            Assert.True(cachedCertificate.HasPrivateKey);

            X509Certificate presentedCertificate = null;
            using(var client = new TcpClient(AddressFamily.InterNetwork))
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cts.Token)
                    .AsTask()
                    .WaitAsync(TestTimeout);

                await using var sslStream = new SslStream(client.GetStream(), false,
                    (_, certificate, _, _) =>
                    {
                        presentedCertificate = certificate;
                        return true;
                    });

                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, cts.Token).WaitAsync(TestTimeout);

                Assert.True(sslStream.IsAuthenticated);
                Assert.True(sslStream.IsEncrypted);
                Assert.True(sslStream.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13,
                    $"Unexpected OS-selected TLS protocol {sslStream.SslProtocol}");
                Assert.NotNull(presentedCertificate);
                using var presentedCertificate2 = X509CertificateLoader.LoadCertificate(
                    presentedCertificate.GetRawCertData());
                Assert.Equal(certificate.Thumbprint, presentedCertificate2.Thumbprint);
            }

            await server.WaitForNoConnectionsAsync(TestTimeout);
        }

        finally
        {
            cts.Cancel();

            if(runTask != null)
                await runTask.WaitAsync(TestTimeout);

            server.RemoveCachedCertificate(pfxFile)?.Dispose();
            File.Delete(pfxFile);
        }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("localhost");
        subjectAlternativeName.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForRejectedSocketAsync(Socket socket)
    {
        var buffer = new byte[1];

        try
        {
            var received = await socket.ReceiveAsync(buffer,
                    SocketFlags.None)
                .WaitAsync(TestTimeout);
            Assert.Equal(0, received);
        }
        catch(SocketException ex)
        {
            Assert.True(ex.SocketErrorCode is SocketError.ConnectionReset or
                SocketError.ConnectionAborted,
                $"Unexpected rejection error {ex.SocketErrorCode}");
        }
    }

    private sealed class TestStratumServer : StratumServer
    {
        public TestStratumServer() : base(
            Substitute.For<IComponentContext>(),
            Substitute.For<IMessageBus>(),
            new RecyclableMemoryStreamManager(),
            Substitute.For<IMasterClock>())
        {
            logger = LogManager.GetCurrentClassLogger();
            clusterConfig = new ClusterConfig { Logging = new ClusterLoggingConfig() };
            poolConfig = new PoolConfig { Id = "tls-test" };
        }

        public TestStratumServer(IComponentContext context, IMessageBus messageBus) :
            base(context, messageBus, new RecyclableMemoryStreamManager(),
                Substitute.For<IMasterClock>())
        {
            logger = LogManager.GetCurrentClassLogger();
            clusterConfig = new ClusterConfig { Logging = new ClusterLoggingConfig() };
            poolConfig = new PoolConfig { Id = "admission-test" };
        }

        public Task AdmitAsync(Share share, Func<Task> acknowledge,
            bool publishShare) =>
            PublishShareAndAcknowledgeAsync(share, acknowledge, publishShare);

        public Func<StratumConnection, Timestamped<JsonRpcRequest>,
            CancellationToken, Task> RequestHandler { get; set; }

        public bool ThrowOnConnect { get; set; }

        public Task RunListenerAsync(CancellationToken ct)
        {
            return RunListenerAsync(ct, new StratumEndpoint(
                new IPEndPoint(IPAddress.Loopback, 0), new PoolEndpoint()));
        }

        public Task RunListenerAsync(CancellationToken ct, StratumEndpoint endpoint)
        {
            var socket = StratumServer.CreateBoundSocket(
                endpoint.IPEndPoint);
            var reservation = new StratumListenerReservation(poolConfig.Id,
                endpoint, socket);
            reservation.Activate();
            return RunAsync(ct, reservation);
        }

        public void SetConnectionDrainTimeout(TimeSpan timeout)
        {
            ConnectionDrainTimeout = timeout;
        }

        public void SetBanManager(IBanManager manager)
        {
            banManager = manager;
        }

        public X509Certificate2 GetCachedCertificate(string path)
        {
            certs.TryGetValue(path, out var certificate);
            return certificate;
        }

        public X509Certificate2 RemoveCachedCertificate(string path)
        {
            certs.TryRemove(path, out var certificate);
            return certificate;
        }

        public async Task WaitForNoConnectionsAsync(TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);

            while(!connections.IsEmpty)
                await Task.Delay(10, timeoutCts.Token);
        }

        public async Task WaitForConnectionCountAsync(int count,
            TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);

            while(connections.Count != count)
                await Task.Delay(10, timeoutCts.Token);
        }

        protected override Task OnRequestAsync(StratumConnection connection,
            Timestamped<JsonRpcRequest> request, CancellationToken ct)
        {
            return RequestHandler?.Invoke(connection, request, ct) ??
                Task.CompletedTask;
        }

        protected override void OnConnect(StratumConnection connection, IPEndPoint endpoint)
        {
            if(ThrowOnConnect)
                throw new InvalidOperationException(
                    "Injected pre-dispatch connection failure");
        }
    }
}
