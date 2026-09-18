using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Miningcore.Configuration;
using Miningcore.Stratum;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public partial class StratumAdmissionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StartupExpiry_IsCountedWithoutErrorsOrBans_IncludingTls(bool auto, bool tls)
    {
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
        logs.Configuration = logging;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), $"admission-{Guid.NewGuid():N}.pfx");
        if(tls) File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        await using var server = new Server(new StratumAdmissionConfig { StartupTimeoutSeconds = 1 },
            tlsAuto: auto, tlsCertificate: tls ? path : null, log: logs.GetLogger("deadline"));
        try
        {
            using var client = await server.Connect();
            if(tls) // Partial TLS record: detection succeeds but authentication cannot finish.
                await client.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x40 });
            await Closed(client);
            await server.Empty();
            server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
            Assert.DoesNotContain(target.Logs, x => x.StartsWith("Error|") || x.StartsWith("Fatal|"));
            Assert.Contains(target.Logs, x => x.Contains("Connection startup deadline expired"));
            Assert.Contains(await Series(server), x => x.Contains("reason=\"startup-timeout\"") && x.EndsWith(" 1"));
        }
        finally
        {
            server.Stop();
            await server.Run;
            server.RemoveCertificate(path);
            if(tls) File.Delete(path);
        }
    }

    [Fact]
    public async Task HostShutdownDuringStartup_IsNotCountedAsDeadlineOrBanned()
    {
        await using var server = new Server(new StratumAdmissionConfig(), tlsAuto: true);
        using var client = await server.Connect();
        await Until(() => server.Accepted == 1);
        server.Stop();
        await server.Run;
        server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        Assert.DoesNotContain(await Series(server), x => x.Contains("reason=\"startup-timeout\""));
    }

    [Theory]
    [InlineData(92, false, true)] // 14-byte prefix + 92 bytes + CRLF = 108 (one over).
    [InlineData(91, false, false)] // Exactly 107 wire bytes.
    [InlineData(47, true, true)] // Fewer than 106 characters, but over 107 UTF-8 bytes.
    public async Task ProxyHeader_EnforcesWireByteLimit(int length, bool multibyte, bool rejected)
    {
        await using var server = new Server(new StratumAdmissionConfig(),
            proxy: new TcpProxyProtocolConfig { Enable = true });
        var header = "PROXY UNKNOWN " + new string(multibyte ? 'é' : 'x', length) + "\r\n";
        if(rejected)
        {
            await server.Rejected(header: header);
            Assert.Equal(0, server.Requests);
        }
        else
            await server.Exchange(header: header);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyHeader_RejectsOverlongPartialLineBeforeDeadline(bool mandatory)
    {
        await using var server = new Server(new StratumAdmissionConfig { StartupTimeoutSeconds = 30 },
            proxy: new TcpProxyProtocolConfig { Enable = true, Mandatory = mandatory });
        using var client = await server.Connect();
        await Send(client, "PROXY UNKNOWN " + new string('x', 93), false);
        await Closed(client); // 15s test timeout is shorter than the 30s startup deadline.
        await server.Empty();
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task OptionalProxy_AllowsOrdinaryJsonLongerThanHeaderLimit()
    {
        await using var server = new Server(new StratumAdmissionConfig(),
            proxy: new TcpProxyProtocolConfig { Enable = true });
        using var client = await server.Connect();
        await Send(client, "{\"id\":1,\"method\":\"ping\",\"params\":[\"" + new string('x', 150) + "\"]}");
        using var reader = new StreamReader(client.GetStream());
        Assert.NotNull(await reader.ReadLineAsync().WaitAsync(Timeout));
    }

    [Fact]
    public async Task PendingIdentities_AreSharedAcrossPorts_AndReleasedAfterIdentificationOrDisconnect()
    {
        await using var server = new Server(new StratumAdmissionConfig { MaxPendingIdentities = 1 },
            proxy: new TcpProxyProtocolConfig { Enable = true, Mandatory = true }, ports: 2);
        using var first = await server.Connect();
        await Until(() => server.Accepted == 1);
        await server.Rejected(1);
        await Send(first, Header("192.0.2.1"), false);
        Assert.NotNull(await Exchange(first));
        using(var next = await server.Connect(1))
        {
            await Until(() => server.Accepted == 2);
            await server.Rejected();
        }
        await Until(() => server.ConnectionAdmission.Snapshot.Active == 1);
        using(var next = await server.Connect(1))
        {
            await Send(next, Header("192.0.2.2"), false);
            Assert.NotNull(await Exchange(next));
        }
        Assert.NotNull(await Exchange(first));
        Assert.Contains(await Series(server), x => x.Contains("reason=\"pending-identity-capacity\"") && x.EndsWith(" 2"));
    }

    [Fact]
    public void PendingIdentityCap_PreservesDirectCapacity_AndFailedIdentityOwnsPendingLeaseUntilDispose()
    {
        var admission = new StratumConnectionAdmission(new StratumAdmissionConfig
            { MaxPendingIdentities = 1, BurstPerAddress = 1 }, new ManualTimeProvider(),
            Guid.NewGuid().ToString("N"), LogManager.CreateNullLogger());
        Assert.True(admission.TryAcquire(IPAddress.Loopback, true, out var proxy));
        Assert.False(admission.TryAcquire(IPAddress.Loopback, true, out _));
        Assert.True(admission.TryAcquire(IPAddress.Loopback, false, out var direct));
        Assert.False(proxy.TrySetIdentity(IPAddress.Loopback));
        Assert.False(admission.TryAcquire(IPAddress.Loopback, true, out _));
        proxy.Dispose();
        proxy.Dispose();
        Assert.True(admission.TryAcquire(IPAddress.Loopback, true, out var replacement));
        replacement.Dispose();
        direct.Dispose();
        admission.Stop();
        Assert.Equal((0, 0), admission.Snapshot);
    }

    [Fact]
    public async Task PoolStop_ClearsGaugesAndIdleIdentities_WithoutAffectingOtherPools()
    {
        await using var server = new Server(new StratumAdmissionConfig());
        await using var other = new Server(new StratumAdmissionConfig());
        await server.Exchange();
        await other.Exchange();
        server.Stop();
        await server.Run;
        Assert.Equal((0, 0), server.ConnectionAdmission.Snapshot);
        var series = await Series(server);
        Assert.All(series.Where(x => x.StartsWith("miningcore_stratum_admission_")), x => Assert.EndsWith(" 0", x));
        Assert.Contains(await Series(other), x => x.StartsWith("miningcore_stratum_admission_addresses{") && x.EndsWith(" 1"));
        await other.Exchange();
    }

    [Fact]
    public async Task OccupancyAndLimits_AllowAlertsBeforeRefusal_AndWarnWithoutIdentityLabels()
    {
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Info, LogLevel.Fatal, target);
        logs.Configuration = logging;
        await using var server = new Server(new StratumAdmissionConfig { MaxConcurrentConnectionsPerAddress = 5 },
            log: logs.GetLogger("headroom"));
        Assert.Contains(target.Logs, x => x.StartsWith("Info|Stratum admission: 100/s burst 200, 4096 concurrent"));
        var clients = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => server.Connect()));
        try
        {
            await Until(() => server.Accepted == 4);
            var series = await Series(server);
            Assert.Contains(series, x => x.StartsWith("miningcore_stratum_admission_max_address_active{") && x.EndsWith(" 4"));
            Assert.Contains(series, x => x.Contains("limit=\"maxConcurrentConnectionsPerAddress\"") && x.EndsWith(" 5"));
            Assert.Single(target.Logs.Where(x => x.StartsWith("Warn|")));
            Assert.DoesNotContain(target.Logs.Where(x => x.StartsWith("Warn|")), x => x.Contains("127.0.0.1"));
        }
        finally { foreach(var client in clients) client.Dispose(); }
        await server.Empty();
        Assert.Contains(await Series(server), x => x.StartsWith("miningcore_stratum_admission_max_address_active{") && x.EndsWith(" 0"));
    }

    [Fact]
    public async Task InvalidPolicy_ReleasesListenerReservation()
    {
        var server = new Server(new StratumAdmissionConfig { Burst = 0 });
        await Assert.ThrowsAsync<ValidationException>(() => server.Run);
        using var rebound = StratumServer.CreateBoundSocket(server.Endpoints[0]);
        await Assert.ThrowsAsync<ValidationException>(async () => await server.DisposeAsync());
    }

    [Fact]
    public async Task SecondRun_IsRejectedAndReleasesItsReservation_WithoutChangingFirstPolicy()
    {
        await using var server = new Server(new StratumAdmissionConfig());
        using var socket = StratumServer.CreateBoundSocket(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = new StratumEndpoint((IPEndPoint) socket.LocalEndPoint, new PoolEndpoint());
        using var reservation = new StratumListenerReservation(server.PoolId, endpoint, socket);
        reservation.Activate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync(CancellationToken.None, reservation));
        using var rebound = StratumServer.CreateBoundSocket(endpoint.IPEndPoint);
        await server.Exchange();
    }

    [Fact]
    public void IdentityTableSizing_IncludesBurstAndRetention_AndRejectsOverflowAndUnsatisfiableLimits()
    {
        var validator = new StratumAdmissionConfigValidator();
        var config = new StratumAdmissionConfig();
        Assert.Equal(16296, config.MinimumTrackedAddresses);
        config.MaxTrackedAddresses = 16296;
        Assert.True(validator.Validate(config).IsValid);
        config.MaxTrackedAddresses--;
        Assert.False(validator.Validate(config).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { ConnectionsPerSecond = 500 }).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { IdleExpirySeconds = 86400, ConnectionsPerSecond = 100000 }).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { ConnectionsPerSecond = int.MaxValue, IdleExpirySeconds = int.MaxValue }).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig { MaxPendingIdentities = 0 }).IsValid);
    }

    [Fact]
    public void EndpointValidation_AcceptsAbsentProxy_AndRejectsInvalidEnabledTrustList()
    {
        var validator = new PoolEndpointValidator();
        Assert.True(validator.Validate(new PoolEndpoint { Difficulty = 1, TcpProxyProtocol = null }).IsValid);
        Assert.False(validator.Validate(new PoolEndpoint { Difficulty = 1,
            TcpProxyProtocol = new TcpProxyProtocolConfig { Enable = true, ProxyAddresses = new[] { "not-an-ip" } } }).IsValid);
    }

    private static async Task<string[]> Series(Server server)
    {
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n')
            .Where(x => x.Contains($"pool=\"{server.PoolId}\"")).ToArray();
    }

    [Fact]
    public async Task TimerCreationFailure_ReleasesListenerReservationAndMetrics()
    {
        var server = new Server(new StratumAdmissionConfig(), new FailingTimerProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.Run);
        using var rebound = StratumServer.CreateBoundSocket(server.Endpoints[0]);
        Assert.All((await Series(server)).Where(x => x.StartsWith("miningcore_stratum_admission_")),
            x => Assert.EndsWith(" 0", x));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.DisposeAsync());
    }

    private sealed class FailingTimerProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("Injected timer failure");
    }

    [Fact]
    public async Task PoolStop_PreservesOccupancyUntilOwnedHandlerActuallyDrains()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new Server(new StratumAdmissionConfig());
        server.ConnectionDrainTimeout = TimeSpan.FromMilliseconds(50);
        server.Handler = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        using var client = await server.Connect();
        await Send(client, "{\"id\":1,\"method\":\"ping\"}");
        await entered.Task.WaitAsync(Timeout);
        try
        {
            server.Stop();
            await server.Run.WaitAsync(Timeout);
            Assert.Equal((1, 1), server.ConnectionAdmission.Snapshot);
            Assert.Contains(await Series(server), x => x.StartsWith("miningcore_stratum_admission_active{") && x.EndsWith(" 1"));
        }
        finally { release.TrySetResult(); }
        await server.Empty();
        Assert.Equal((0, 0), server.ConnectionAdmission.Snapshot);
        Assert.All((await Series(server)).Where(x => x.StartsWith("miningcore_stratum_admission_")),
            x => Assert.EndsWith(" 0", x));
    }

    [Fact]
    public void DefaultConcurrencyBoundary_IsExact_AndPolicyIsASnapshot()
    {
        var config = new StratumAdmissionConfig();
        var time = new ManualTimeProvider();
        var admission = new StratumConnectionAdmission(config, time, Guid.NewGuid().ToString("N"), LogManager.CreateNullLogger());
        config.MaxConcurrentConnections = 1; // Mutating caller configuration cannot reload half a policy.
        var leases = new StratumConnectionAdmission.Lease[4096];
        try
        {
            for(var i = 0; i < leases.Length; i++)
            {
                time.AdvanceMonotonic(TimeSpan.FromMilliseconds(10));
                Assert.True(admission.TryAcquire(new IPAddress(new byte[] { 192, 0, (byte) (i >> 8), (byte) i }), false, out leases[i]));
            }
            Assert.False(admission.TryAcquire(IPAddress.Loopback, false, out _));
            Assert.Equal((4096, 4096), admission.Snapshot);
        }
        finally
        {
            foreach(var lease in leases) lease?.Dispose();
            admission.Stop();
        }
        Assert.Equal((0, 0), admission.Snapshot);
    }
}
