using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Banning;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public partial class StratumAdmissionTests
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Rejections_HaveBoundedMetricsAndMonotonicLogSummaries_WithoutBans()
    {
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
        logs.Configuration = logging;
        var time = new ManualTimeProvider();
        await using var server = new Server(new StratumAdmissionConfig { BurstPerAddress = 1 },
            time, log: logs.GetLogger("admission"));
        await server.Exchange();
        for(var i = 0; i < 20; i++) await server.Rejected();
        Assert.Single(target.Logs);
        time.MoveWallClock(TimeSpan.FromDays(1000));
        await server.Rejected();
        Assert.Single(target.Logs);
        time.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        await server.Exchange();
        await server.Rejected();
        Assert.Equal(2, target.Logs.Count);
        Assert.Contains("suppressed since last summary: 20", target.Logs[1]);
        Assert.DoesNotContain(target.Logs, x => x.Contains("127.0.0.1"));
        server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);

        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        var series = Encoding.UTF8.GetString(output.ToArray()).Split('\n')
            .Where(x => x.Contains($"pool=\"{server.PoolId}\"")).ToArray();
        Assert.Contains(series, x => x.StartsWith("miningcore_stratum_connections_admission_total{") &&
            x.Contains("reason=\"address-rate\"") && x.EndsWith(" 22"));
        Assert.Contains(series, x => x.StartsWith("miningcore_stratum_admission_active{") && x.EndsWith(" 0"));
        Assert.DoesNotContain(series, x => x.Contains("127.0.0.1") || x.Contains("connection="));
    }

    [Fact]
    public async Task DefaultPolicy_Allows32SharedAddressStartups_AndKeepsEstablishedMiners()
    {
        await using var server = new Server(new StratumAdmissionConfig(), new ManualTimeProvider());
        var clients = new List<TcpClient>();
        try
        {
            for(var i = 0; i < 32; i++)
            {
                var client = await server.Connect();
                clients.Add(client);
                Assert.NotNull(await Exchange(client));
            }
            await server.Rejected();
            Assert.Equal(32, server.Accepted);
            Assert.NotNull(await Exchange(clients[0]));
        }
        finally { foreach(var client in clients) client.Dispose(); }
        await server.Empty();
    }

    [Fact]
    public async Task RealTimer_ExpiresInactiveStateWithoutFurtherTraffic()
    {
        await using var server = new Server(new StratumAdmissionConfig
        { BurstPerAddress = 1, IdleExpirySeconds = 1 });
        await server.Exchange();
        await Until(() => server.ConnectionAdmission.Snapshot.Addresses == 0);
        await server.Exchange();
    }

    [Fact]
    public async Task AcceptFailureAndTaskObserver_ReleaseCapacityExactlyOnce()
    {
        await using var server = new Server(new StratumAdmissionConfig { MaxConcurrentConnections = 1 });
        server.ThrowOnConnect = true;
        await server.Rejected();
        await server.Empty();
        server.ThrowOnConnect = false;
        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeRemoval = () => { removing.TrySetResult(); return release.Task; };
        using(var client = await server.Connect()) Assert.NotNull(await Exchange(client));
        try
        {
            await removing.Task.WaitAsync(Timeout);
            await server.Rejected();
            Assert.Equal(1, server.ConnectionAdmission.Snapshot.Active);
        }
        finally { release.TrySetResult(); }
        await server.Empty();
        await server.Exchange();
    }

    [Fact]
    public async Task Churn_IsBoundedAcrossListeners_RecoversWithMonotonicTime_AndExpires()
    {
        var time = new ManualTimeProvider();
        await using var server = new Server(new StratumAdmissionConfig
        {
            Burst = 1000, BurstPerAddress = 4, ConnectionsPerSecondPerAddress = 1,
            IdleExpirySeconds = 4,
        }, time, ports: 2);
        for(var i = 0; i < 4; i++) await server.Exchange(i % 2);
        for(var i = 0; i < 100; i++) await server.Rejected(i % 2);
        Assert.Equal(4, server.Accepted);
        Assert.Equal((0, 1), server.ConnectionAdmission.Snapshot);
        time.MoveWallClock(TimeSpan.FromDays(365));
        await server.Rejected();
        time.MoveWallClock(TimeSpan.FromDays(-730));
        await server.Rejected();
        time.AdvanceMonotonic(TimeSpan.FromMilliseconds(999));
        await server.Rejected();
        time.AdvanceMonotonic(TimeSpan.FromMilliseconds(1));
        await server.Exchange();
        time.AdvanceMonotonic(TimeSpan.FromSeconds(4));
        server.ConnectionAdmission.SweepIdle();
        Assert.Equal((0, 0), server.ConnectionAdmission.Snapshot);
        for(var i = 0; i < 4; i++) await server.Exchange();
        await server.Rejected();
    }

    [Fact]
    public async Task SustainedChurn_OnlyReceivesRefilledAllowanceAfterInitialBurst()
    {
        var time = new ManualTimeProvider();
        await using var server = new Server(new StratumAdmissionConfig
        { BurstPerAddress = 4, ConnectionsPerSecondPerAddress = 1 }, time);
        for(var i = 0; i < 4; i++) await server.Exchange();
        for(var second = 0; second < 8; second++)
        {
            for(var i = 0; i < 20; i++) await server.Rejected();
            Assert.Equal(4 + second, server.Accepted);
            time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
            await server.Exchange();
            Assert.Equal((0, 1), server.ConnectionAdmission.Snapshot);
        }
        Assert.Equal(12, server.Accepted);
    }

    [Fact]
    public async Task ActiveProxyIdentities_DoNotExpireOrEvict_WhenOtherClientsArrive()
    {
        var time = new ManualTimeProvider();
        await using var server = new Server(new StratumAdmissionConfig
        { BurstPerAddress = 1, IdleExpirySeconds = 1 }, time,
            new TcpProxyProtocolConfig { Enable = true, Mandatory = true });
        using(var client = await server.Connect())
        {
            await Send(client, Header("192.0.2.1"), false);
            Assert.NotNull(await Exchange(client));
            time.AdvanceMonotonic(TimeSpan.FromDays(1));
            server.ConnectionAdmission.SweepIdle();
            Assert.Equal((1, 1), server.ConnectionAdmission.Snapshot);
            // A new identity must never evict an active identity, even after idle expiry.
            using(var other = await server.Connect())
            {
                await Send(other, Header("192.0.2.2"), false);
                Assert.NotNull(await Exchange(other));
            }
            Assert.NotNull(await Exchange(client));
        }
        await server.Empty();
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await server.Exchange(header: Header("192.0.2.2"));
    }

    [Fact]
    public async Task ConcurrentSharedAddressBurst_IsAtomic_AndExistingMinersRemainUsable()
    {
        await using var server = new Server(new StratumAdmissionConfig
        { MaxConcurrentConnectionsPerAddress = 8, BurstPerAddress = 100 }, new ManualTimeProvider());
        var clients = new TcpClient[40];
        try
        {
            await Task.WhenAll(Enumerable.Range(0, clients.Length).Select(async i =>
            {
                try { clients[i] = await server.Connect(); }
                catch(SocketException ex) when(ex.SocketErrorCode is SocketError.ConnectionReset or
                    SocketError.ConnectionAborted)
                {
                    // Immediate abortive refusal can reach Linux before ConnectAsync
                    // completes, or later during exchange. Both are the same refusal.
                }
            }));
            await Until(() => server.Accepted == 8);
            var replies = await Task.WhenAll(clients.Select(async client =>
            {
                if(client == null) return null;
                try { return await Exchange(client); }
                catch(IOException ex) when(ex.InnerException is SocketException { SocketErrorCode:
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown }) { return null; }
            }));
            Assert.Equal(8, replies.Count(x => x != null));
            Assert.Equal(8, server.ConnectionAdmission.Snapshot.Active);
            Assert.Equal(8, server.TrackedConnectionTaskCount);
            var survivor = clients[Array.FindIndex(replies, x => x != null)];
            Assert.NotNull(await Exchange(survivor));
            await server.Rejected();
        }
        finally { foreach(var client in clients) client?.Dispose(); }
        await server.Empty();
        await server.Exchange();
    }

    [Fact]
    public async Task PoolRateAndConcurrency_AreIndependentOfAddressAndOtherPools()
    {
        await using var server = new Server(new StratumAdmissionConfig
        { Burst = 2, ConnectionsPerSecond = 1, MaxConcurrentConnections = 1 }, new ManualTimeProvider());
        await using var other = new Server(new StratumAdmissionConfig(), new ManualTimeProvider());
        using(var first = await server.Connect())
        {
            Assert.NotNull(await Exchange(first));
            await server.Rejected();
            await other.Exchange();
        }
        await server.Empty();
        await server.Exchange();
        await server.Rejected();
        await other.Exchange();
    }

    [Fact]
    public async Task RefusedDirectAddress_DoesNotConsumeOtherAddressesPoolTokens()
    {
        await using var server = new Server(new StratumAdmissionConfig { Burst = 2, BurstPerAddress = 1 }, new ManualTimeProvider());
        await server.Exchange();
        for(var i = 0; i < 30; i++) await server.Rejected();
        using(var other = await server.Connect(source: IPAddress.Parse("127.0.0.2")))
            Assert.NotNull(await Exchange(other));
        await server.Empty();
        Assert.Equal(2, server.Accepted);
        await server.Rejected();
    }

    [Fact]
    public async Task OwnedShare_KeepsCapacityUntilPersistenceCompletes_EvenAfterPeerDisconnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new Server(new StratumAdmissionConfig { MaxConcurrentConnections = 1, StartupTimeoutSeconds = 1 });
        var share = new Miningcore.Blockchain.Share();
        share.SetPersistenceAdmission(persisted.Task);
        var acknowledgements = 0;
        CancellationToken handlerToken = default;
        server.Handler = async (connection, request, ct) =>
        {
            handlerToken = ct;
            entered.TrySetResult();
            await server.Account(share, () => { acknowledgements++; return Task.CompletedTask; });
        };
        using(var client = await server.Connect())
        {
            await Send(client, "{\"id\":1,\"method\":\"mining.submit\",\"params\":[]}");
            await entered.Task.WaitAsync(Timeout);
            await Task.Delay(1100);
            Assert.False(handlerToken.IsCancellationRequested);
        }
        await server.Rejected();
        Assert.Equal(1, server.ConnectionAdmission.Snapshot.Active);
        Assert.Equal(1, server.TrackedConnectionTaskCount);
        Assert.Equal(0, acknowledgements);
        persisted.TrySetResult();
        await server.Empty();
        Assert.Equal(1, acknowledgements);
        server.Bus.Received(1).SendMessage(share, Arg.Any<string>());
        server.Handler = null;
        await server.Exchange();
    }

    [Fact]
    public async Task TrustedProxy_SeparatesClients_AndDoesNotRefreshExhaustedEntries()
    {
        var time = new ManualTimeProvider();
        await using var server = new Server(new StratumAdmissionConfig
        { BurstPerAddress = 1, ConnectionsPerSecondPerAddress = 1, IdleExpirySeconds = 2 },
            time, new TcpProxyProtocolConfig { Enable = true, Mandatory = true });
        await server.Exchange(header: Header("192.0.2.1"));
        await server.Rejected(header: Header("192.0.2.1"));
        await server.Exchange(header: Header("192.0.2.2"));
        await server.Rejected(header: Header("192.0.2.2"));
        Assert.Equal((0, 2), server.ConnectionAdmission.Snapshot);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        await server.Exchange(header: Header("192.0.2.3"));
        Assert.Equal((0, 1), server.ConnectionAdmission.Snapshot);
    }

    [Theory]
    [InlineData("PROXY TCP4 192.0.2.1 127.0.0.1 123 3333\r\n", false)]
    [InlineData("PROXY TCP4 192.0.2.1 127.0.0.1 123 3333\n", true)]
    [InlineData("PROXY TCP4 ::1 127.0.0.1 123 3333\r\n", true)]
    [InlineData("PROXY TCP4 127.1 127.0.0.1 123 3333\r\n", true)]
    [InlineData("PROXY TCP4 192.0.2.1 127.0.0.1 -1 3333\r\n", true)]
    [InlineData("PROXY TCP4 192.0.2.1 127.0.0.1 65536 3333\r\n", true)]
    [InlineData("PROXY TCP6 fe80::1%2 ::1 123 3333\r\n", true)]
    [InlineData("PROXY TCP4\r\n", true)]
    [InlineData("PROXY TCP4 192.0.2.1 127.0.0.1 123 3333 extra\r\n", true)]
    [InlineData("", true)]
    public async Task ProxySpoofOrMalformedHeader_NeverDispatches(string header, bool trusted)
    {
        await using var server = new Server(new StratumAdmissionConfig(), proxy: new TcpProxyProtocolConfig
        { Enable = true, Mandatory = true, ProxyAddresses = new[] { trusted ? "127.0.0.1" : "192.0.2.100" } });
        await server.Rejected(header: header);
        Assert.Equal(0, server.Requests);
        Assert.Equal(0, server.ConnectionAdmission.Snapshot.Active);
    }

    [Theory]
    [InlineData("PROXY UNKNOWN ignored\r\n")]
    [InlineData("")]
    [InlineData("PROXY TCP6 ::ffff:127.0.0.1 ::1 123 3333\r\n")]
    public async Task ProxyWithoutUsableIdentity_UsesPeerAndNormalizesMappedIPv4(string header)
    {
        await using var server = new Server(new StratumAdmissionConfig { BurstPerAddress = 1 },
            new ManualTimeProvider(), new TcpProxyProtocolConfig { Enable = true });
        await server.Exchange(header: header);
        await server.Rejected(header: Header("127.0.0.1"));
        Assert.Equal(1, server.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupDeadline_ReleasesSilentAndPartialHeaderConnections(bool tlsAuto)
    {
        await using var server = new Server(new StratumAdmissionConfig { StartupTimeoutSeconds = 1 },
            proxy: new TcpProxyProtocolConfig { Enable = true, Mandatory = true }, tlsAuto: tlsAuto);
        using var client = await server.Connect();
        if(!tlsAuto) await Send(client, "PROXY TCP4 192.0.2.", newline: false);
        await Closed(client);
        await server.Empty();
        Assert.Equal(0, server.Requests);
        Assert.Equal((0, 0), server.ConnectionAdmission.Snapshot);
    }

    [Fact]
    public void Config_RejectsUnboundedAndPrematureExpiry()
    {
        var validator = new StratumAdmissionConfigValidator();
        Assert.True(validator.Validate(new StratumAdmissionConfig()).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { Burst = 0 }).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { MaxTrackedAddresses = int.MaxValue }).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { IdleExpirySeconds = 1 }).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { StartupTimeoutSeconds = 0 }).IsValid);
    }

    internal static string Header(string address) => $"PROXY TCP4 {address} 127.0.0.1 123 3333\r\n";
    internal static Task Send(TcpClient client, string text, bool newline = true) =>
        client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text + (newline ? "\n" : ""))).AsTask();
    internal static async Task<string> Exchange(TcpClient client)
    {
        await Send(client, "{\"id\":1,\"method\":\"ping\",\"params\":[]}");
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
        return await reader.ReadLineAsync().WaitAsync(Timeout);
    }
    internal static async Task Closed(TcpClient client)
    {
        try { Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout)); }
        catch(IOException ex) when(ex.InnerException is SocketException { SocketErrorCode:
            SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
    }
    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while(!condition()) await Task.Delay(5, timeout.Token);
    }

    internal sealed class Server : StratumServer, IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        private readonly IPEndPoint[] endpoints;
        private int accepted;
        private int requests;
        internal int Accepted => Volatile.Read(ref accepted);
        internal int Requests => Volatile.Read(ref requests);
        internal IMessageBus Bus => messageBus;
        internal IBanManager Bans => banManager;
        internal string PoolId => poolConfig.Id;
        internal Task Run => run;
        internal IPEndPoint[] Endpoints => endpoints;
        internal void Stop() => stop.Cancel();
        internal void RemoveCertificate(string path)
        {
            if(certs.TryRemove(path, out var cert)) cert.Dispose();
        }
        internal Func<StratumConnection, Timestamped<JsonRpcRequest>, CancellationToken, Task> Handler;
        internal Func<Task> BeforeRemoval;
        internal bool ThrowOnConnect;
        internal Server(StratumAdmissionConfig config, TimeProvider time = null,
            TcpProxyProtocolConfig proxy = null, int ports = 1, bool tlsAuto = false, ILogger log = null,
            string tlsCertificate = null) : base(
            new ContainerBuilder().Build(), Substitute.For<IMessageBus>(), new RecyclableMemoryStreamManager(),
            Substitute.For<IMasterClock>())
        {
            logger = log ?? new NullLogger(LogManager.LogFactory);
            banManager = Substitute.For<IBanManager>();
            clusterConfig = new ClusterConfig { Banning = new ClusterBanningConfig { BanOnJunkReceive = true } };
            // Isolate static Prometheus children across concurrently running tests.
            poolConfig = new PoolConfig { Id = Guid.NewGuid().ToString("N"), ConnectionAdmission = config };
            AdmissionTimeProvider = time ?? TimeProvider.System;
            var reservations = Enumerable.Range(0, ports).Select(_ =>
            {
                var socket = CreateBoundSocket(new IPEndPoint(IPAddress.Loopback, 0));
                var endpoint = new StratumEndpoint((IPEndPoint) socket.LocalEndPoint,
                    new PoolEndpoint { TcpProxyProtocol = proxy, TlsAuto = tlsAuto,
                        Tls = tlsCertificate != null, TlsPfxFile = tlsCertificate });
                var reservation = new StratumListenerReservation(poolConfig.Id, endpoint, socket);
                reservation.Activate();
                return reservation;
            }).ToArray();
            endpoints = reservations.Select(x => x.Endpoint.IPEndPoint).ToArray();
            run = RunAsync(stop.Token, reservations);
        }
        internal async Task<TcpClient> Connect(int port = 0, IPAddress source = null)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            if(source != null) client.Client.Bind(new IPEndPoint(source, 0));
            try { await client.ConnectAsync(endpoints[port]).WaitAsync(Timeout); return client; }
            catch { client.Dispose(); throw; }
        }
        internal async Task Exchange(int port = 0, string header = null)
        {
            using(var client = await Connect(port))
            {
                if(header != null) await Send(client, header, false);
                Assert.NotNull(await StratumAdmissionTests.Exchange(client));
            }
            await Empty();
        }
        internal async Task Rejected(int port = 0, string header = null)
        {
            try
            {
                using var client = await Connect(port);
                if(header != null)
                {
                    try { await Send(client, header + "{\"id\":1,\"method\":\"ping\"}"); }
                    catch(IOException ex) when(ex.InnerException is SocketException) { }
                }
                await Closed(client);
            }
            catch(SocketException ex) when(ex.SocketErrorCode is SocketError.ConnectionReset or
                SocketError.ConnectionAborted or SocketError.Shutdown) { }
            if(header != null) await Empty();
        }
        internal Task Empty() => Until(() => TrackedConnectionTaskCount == 0 && ConnectionAdmission.Snapshot.Active == 0);
        internal Task Account(Miningcore.Blockchain.Share share, Func<Task> acknowledge) => PublishShareAndAcknowledgeAsync(share, acknowledge);
        protected override Task BeforeConnectionTaskRemovalAsync(string id) => BeforeRemoval?.Invoke() ?? Task.CompletedTask;
        protected override void OnConnect(StratumConnection connection, IPEndPoint endpoint)
        {
            Interlocked.Increment(ref accepted);
            if(ThrowOnConnect) throw new InvalidOperationException("Injected setup failure");
        }
        protected override Task OnRequestAsync(StratumConnection connection, Timestamped<JsonRpcRequest> request, CancellationToken ct)
        {
            Interlocked.Increment(ref requests);
            return Handler?.Invoke(connection, request, ct) ?? connection.RespondAsync(connection.RemoteEndpoint.Address.ToString(), request.Value.Id);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await run.WaitAsync(Timeout); }
            finally
            {
                stop.Dispose();
                ((IDisposable) ctx).Dispose();
            }
        }
    }
}
