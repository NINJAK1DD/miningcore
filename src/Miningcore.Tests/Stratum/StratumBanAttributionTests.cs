using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Banning;
using Miningcore.Configuration;
using Miningcore.Stratum;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public partial class StratumAdmissionTests
{
    [NonLoopbackTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBan_ProtectsEveryClusterProxy_FromForwardedClaimsAndOtherPoolJunk(bool tls)
    {
        var proxyAddress = BanTransportAddress();
        var secondProxy = IPAddress.Parse("192.0.2.254");
        var proxyEndpoint = new PoolEndpoint { TcpProxyProtocol = new TcpProxyProtocolConfig
            { Enable = true, Mandatory = true, ProxyAddresses = new[] { proxyAddress.ToString() } } };
        var directEndpoint = new PoolEndpoint();
        // The second proxy is trusted by a different pool. It need not be online
        // for its protection to apply to the first listener that starts.
        var secondEndpoint = new PoolEndpoint { TcpProxyProtocol = new TcpProxyProtocolConfig
            { Enable = true, ProxyAddresses = new[] { "::ffff:192.0.2.254" } } };
        var cluster = new ClusterConfig
        {
            Banning = new ClusterBanningConfig { BanOnJunkReceive = true },
            Pools = new[] { proxyEndpoint, secondEndpoint, directEndpoint }.Select((endpoint, i) => new PoolConfig
            {
                Id = $"cluster-proxy-{i}", Enabled = true, EnableInternalStratum = true,
                Ports = new System.Collections.Generic.Dictionary<int, PoolEndpoint> { [3333 + i] = endpoint },
            }).ToArray(),
        };
        var bans = new IntegratedBanManager();
        await using var proxy = new BanLab(true, tls, bans: bans, cluster: cluster, endpointConfig: proxyEndpoint);
        await using var direct = new BanLab(false, tls, bans: bans, cluster: cluster, endpointConfig: directEndpoint);
        using var healthy = await BanSession.Connect(proxy);
        await healthy.Send(Header("192.0.2.212"));
        Assert.NotNull(await healthy.Exchange());
        using(var claim = await BanSession.Connect(proxy))
        {
            await claim.Send(BanHeader(secondProxy, true) + "{junk:!}\n");
            await claim.Closed();
        }
        using(var junk = await BanSession.Connect(direct))
        {
            await junk.Send("{junk:!}\n");
            await junk.Closed();
        }
        await direct.Server.Empty();
        await AssertAutomaticBanMetric(proxy.Server.PoolId, "suppressed", "trusted-proxy");
        await AssertAutomaticBanMetric(direct.Server.PoolId, "suppressed", "trusted-proxy");
        Assert.False(bans.IsBanned(secondProxy));
        Assert.False(bans.IsBanned(proxyAddress));
        Assert.NotNull(await healthy.Exchange());
        using var subsequent = await BanSession.Connect(proxy);
        await subsequent.Send(Header("192.0.2.213"));
        Assert.NotNull(await subsequent.Exchange());
    }

    [NonLoopbackTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalProxy_UntrustedHeaderlessJunk_BansActualSocketPeer(bool tls)
    {
        await using var lab = new BanLab(false, tls, mandatory: false, enableUntrustedProxy: true);
        using(var client = await BanSession.Connect(lab))
        {
            await client.Send("{junk:!}\n");
            await client.Closed();
        }
        await lab.Server.Empty();
        lab.Server.Bans.Received(1).Ban(lab.Address, TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void ClusterTrust_IsNormalizedFrozenAndLimitedToEnabledStratumListeners()
    {
        PoolConfig Pool(string address, bool enabled = true, bool stratum = true, bool proxy = true) => new()
        {
            Enabled = enabled, EnableInternalStratum = stratum,
            Ports = new System.Collections.Generic.Dictionary<int, PoolEndpoint> { [3333] = new()
            { TcpProxyProtocol = new() { Enable = proxy, ProxyAddresses = new[] { address } } } },
        };
        var active = Pool("::ffff:192.0.2.254");
        var cluster = new ClusterConfig { Pools = new[] { active,
            Pool("192.0.2.1", enabled: false), Pool("192.0.2.2", stratum: false), Pool("192.0.2.3", proxy: false) } };
        var snapshot = StratumClusterProxyPolicy.For(cluster);
        active.Ports[3333].TcpProxyProtocol.ProxyAddresses[0] = "192.0.2.4";
        Assert.Same(snapshot, StratumClusterProxyPolicy.For(cluster));
        Assert.True(snapshot.IsTrustedProxy(IPAddress.Parse("192.0.2.254")));
        Assert.True(snapshot.ForListener(active.Ports[3333]).IsTrustedPeer(IPAddress.Parse("192.0.2.254")));
        foreach(var suffix in new[] { 1, 2, 3, 4 })
            Assert.False(snapshot.IsTrustedProxy(IPAddress.Parse($"192.0.2.{suffix}")));
        Assert.False(StratumClusterProxyPolicy.For(new ClusterConfig()).IsTrustedProxy(IPAddress.Parse("192.0.2.254")));
        Assert.Throws<InvalidOperationException>(() => snapshot.ForListener(Pool("192.0.2.5").Ports[3333]));
    }

    [Fact]
    public async Task AutomaticBan_ResultAndMetricsDistinguishAppliedFromUnavailable()
    {
        await using var server = new Server(new StratumAdmissionConfig());
        server.Handler = (connection, _, _) => connection.RespondAsync(server.BanAutomatically(connection), 1);
        using(var client = await server.Connect())
            Assert.True(Newtonsoft.Json.Linq.JObject.Parse(await Exchange(client))["result"].Value<bool>());
        await server.Empty();
        server.Bans.Received(1).Ban(IPAddress.Loopback, TimeSpan.FromMinutes(3));
        server.DisableBanManager();
        using(var client = await server.Connect())
            Assert.False(Newtonsoft.Json.Linq.JObject.Parse(await Exchange(client))["result"].Value<bool>());
        await server.Empty();
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        var series = Encoding.UTF8.GetString(output.ToArray()).Split('\n')
            .Where(x => x.StartsWith("miningcore_stratum_automatic_bans_total{") && x.Contains($"pool=\"{server.PoolId}\"")).ToArray();
        Assert.Equal(2, series.Length);
        Assert.Contains(series, x => x.Contains("outcome=\"applied\"") && x.Contains("reason=\"none\"") && x.EndsWith(" 1"));
        Assert.Contains(series, x => x.Contains("outcome=\"unavailable\"") && x.Contains("reason=\"none\"") && x.EndsWith(" 1"));
        Assert.DoesNotContain(series, x => x.Contains("127.0.0.1") || x.Contains("connection="));
    }

    [Fact]
    public async Task IntegratedLoopbackExemption_ReportsFixedSuppressionReason()
    {
        var bans = new IntegratedBanManager();
        await using var server = new Server(new StratumAdmissionConfig(), bans: bans,
            listenAddress: IPAddress.Loopback);
        server.Handler = (connection, _, _) => connection.RespondAsync(server.BanAutomatically(connection), 1);
        using(var client = await server.Connect())
            Assert.False(Newtonsoft.Json.Linq.JObject.Parse(await Exchange(client))["result"].Value<bool>());
        await server.Empty();
        Assert.False(bans.IsBanned(IPAddress.Loopback));
        await AssertAutomaticBanMetric(server.PoolId, "suppressed", "loopback");
    }
    [NonLoopbackTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task BannedProxyIdentity_IsRejectedOnHeaderAlone_BeforeAdmissionOrCoalescedJunk(
        bool tls, bool mapped, bool junk)
    {
        await using var lab = new BanLab(true, tls, config: new StratumAdmissionConfig
        { MaxPendingIdentities = 1, MaxConcurrentConnections = 1, StartupTimeoutSeconds = 30 });
        var banned = IPAddress.Parse("192.0.2.211");
        lab.Server.Bans.IsBanned(banned).Returns(true);
        using(var client = await BanSession.Connect(lab))
        {
            await client.Send(BanHeader(banned, mapped) + (junk ? "{synthetic-secret:!}\n" : ""));
            await client.Closed(); // No JSON needed; shorter than the startup deadline.
        }
        await lab.Server.Empty();
        Assert.Equal((0, 0), lab.Server.ConnectionAdmission.Snapshot);
        Assert.Equal(0, lab.Server.Requests);
        lab.Server.Bans.Received().IsBanned(banned);
        lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        using(var healthy = await BanSession.Connect(lab))
        {
            await healthy.Send(Header("192.0.2.212"));
            Assert.NotNull(await healthy.Exchange());
        }
        await lab.Server.Empty();
        Assert.Equal(1, lab.Server.Requests);
    }

    [NonLoopbackTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LateClientBan_RejectsNextRequest_WhileHealthyClientAndOtherPoolContinue(bool proxy, bool tls)
    {
        await using var lab = new BanLab(proxy, tls);
        await using var other = new BanLab(false, tls);
        using var client = await BanSession.Connect(lab);
        using var healthy = await BanSession.Connect(lab, source: proxy ? lab.Address : IPAddress.Loopback);
        var identity = proxy ? IPAddress.Parse("192.0.2.211") : lab.Address;
        if(proxy)
        {
            await client.Send(BanHeader(identity, true));
            await healthy.Send(Header("192.0.2.212"));
        }
        Assert.NotNull(await client.Exchange());
        Assert.NotNull(await healthy.Exchange());
        lab.Server.Bans.IsBanned(identity).Returns(true);
        await client.Send("{\"id\":2,\"method\":\"ping\"}\n");
        await client.Closed();
        Assert.NotNull(await healthy.Exchange());
        using var unrelated = await BanSession.Connect(other);
        Assert.NotNull(await unrelated.Exchange());
        Assert.Equal(3, lab.Server.Requests);
        lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
    }

    [NonLoopbackTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitProxyTransportBan_IsEnforcedAtAcceptAndOnEstablishedForwardedRequests(bool tls)
    {
        await using var lab = new BanLab(true, tls);
        using var client = await BanSession.Connect(lab);
        await client.Send(Header("192.0.2.211"));
        Assert.NotNull(await client.Exchange());
        lab.Server.Bans.IsBanned(lab.Address).Returns(true);
        await client.Send("{\"id\":2,\"method\":\"ping\"}\n");
        await client.Closed();
        await lab.Server.Empty();
        var accepted = lab.Server.Accepted;
        await lab.Server.Rejected(); // Reject transport before TLS or registration.
        Assert.Equal(accepted, lab.Server.Accepted);
        lab.Server.Bans.IsBanned(lab.Address).Returns(false);
        using var recovery = await BanSession.Connect(lab);
        await recovery.Send(Header("192.0.2.212"));
        Assert.NotNull(await recovery.Exchange());
        lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
    }

    [NonLoopbackTheory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "PROXY UNKNOWN ignored\r\n")]
    [InlineData(true, "PROXY UNKNOWN ignored\r\n")]
    [InlineData(false, "self")]
    [InlineData(true, "self")]
    public async Task TrustedProxyWithoutDistinctClientIdentity_DoesNotReceiveAutomaticJunkOrPoolBan(bool tls, string header)
    {
        using var factory = new LogFactory();
        var messages = new MemoryTarget { Layout = "${message}" };
        var logging = new LoggingConfiguration();
        logging.AddRule(LogLevel.Info, LogLevel.Fatal, messages);
        factory.Configuration = logging;
        await using var lab = new BanLab(true, tls, mandatory: false, log: factory.GetLogger("suppression"));
        if(header == "self") header = BanHeader(lab.Address, true);
        using(var client = await BanSession.Connect(lab))
        {
            await client.Send(header + "{synthetic-secret:!}\n");
            await client.Closed();
        }
        await lab.Server.Empty();
        lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        Assert.DoesNotContain(messages.Logs, message => message.Contains("Banning client", StringComparison.Ordinal));
        Assert.Contains(messages.Logs, message => message.Contains("Automatic client ban suppressed (reason: unattributed)", StringComparison.Ordinal));
        // Exercise the same helper used by login, invalid-share and miner-effort
        // policies. These callers still disconnect/reject the offending session.
        lab.Server.Handler = (connection, _, _) =>
        {
            lab.Server.BanAutomatically(connection);
            return connection.RespondAsync(true, 1);
        };
        using(var client = await BanSession.Connect(lab))
        {
            await client.Send(header);
            Assert.NotNull(await client.Exchange());
        }
        await lab.Server.Empty();
        lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
        lab.Server.Handler = null;
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        Assert.Contains(Encoding.UTF8.GetString(output.ToArray()).Split('\n'), x =>
            x.StartsWith("miningcore_stratum_automatic_bans_total{") && x.Contains($"pool=\"{lab.Server.PoolId}\"") &&
            x.Contains("outcome=\"suppressed\"") && x.Contains("reason=\"unattributed\"") && x.EndsWith(" 2"));
        using var healthy = await BanSession.Connect(lab);
        await healthy.Send(Header("192.0.2.212"));
        Assert.NotNull(await healthy.Exchange());
    }

    [NonLoopbackTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreIdentityTlsAuthenticationFailure_BansOnlyDirectTransport(bool proxy, bool auto)
    {
        using var factory = new LogFactory();
        var messages = new MemoryTarget { Layout = "${message}" };
        var logging = new LoggingConfiguration();
        logging.AddRule(LogLevel.Info, LogLevel.Fatal, messages);
        factory.Configuration = logging;
        await using var lab = new BanLab(proxy, true, tlsAuto: auto, log: factory.GetLogger("tls-ban-result"));
        using(var client = await lab.Server.Connect(source: lab.Address))
        {
            // Invalid TLS handshake record, complete enough to fail before deadline.
            await client.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x04, 0xff, 0x00, 0x00, 0x00 });
            await ClosedAfterTlsAlert(client);
        }
        await lab.Server.Empty();
        lab.Server.Bans.Received(proxy ? 0 : 1).Ban(lab.Address, TimeSpan.FromMinutes(3));
        if(proxy)
        {
            lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
            Assert.DoesNotContain(messages.Logs, x => x.Contains("Banning client", StringComparison.Ordinal));
            Assert.Contains(messages.Logs, x => x.Contains("Automatic client ban suppressed (reason: unattributed)", StringComparison.Ordinal));
            await AssertAutomaticBanMetric(lab.Server.PoolId, "suppressed", "unattributed");
        }
        else Assert.Contains(messages.Logs, x => x.Contains("Banning client for failing SSL handshake", StringComparison.Ordinal));
        using var healthy = await BanSession.Connect(lab);
        if(proxy) await healthy.Send(Header("192.0.2.212"));
        Assert.NotNull(await healthy.Exchange());
    }

    [NonLoopbackTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedProxyClaim_CannotBanOrAdmitTheClaimedVictim(bool tls)
    {
        await using var lab = new BanLab(false, tls, enableUntrustedProxy: true);
        var victim = IPAddress.Parse("192.0.2.211");
        using(var client = await BanSession.Connect(lab))
        {
            await client.Send(Header(victim.ToString()) + "{synthetic-secret:!}\n");
            await client.Closed();
        }
        await lab.Server.Empty();
        Assert.Equal(0, lab.Server.Requests);
        lab.Server.Bans.DidNotReceive().IsBanned(victim);
        lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
    }

    [NonLoopbackTheory]
    [InlineData(false, false, "missing", false)]
    [InlineData(false, true, "disabled", false)]
    [InlineData(false, true, "unset", true)]
    [InlineData(true, false, "missing", false)]
    [InlineData(true, true, "disabled", false)]
    [InlineData(true, true, "unset", true)]
    [InlineData(true, true, "enabled", true)]
    public async Task AttributedJunkBan_PreservesConfigurationAndNormalizedTarget(bool proxy, bool tls, string policy, bool banned)
    {
        await using var lab = new BanLab(proxy, tls);
        lab.Server.SetJunkBanPolicy(policy);
        var identity = proxy ? IPAddress.Parse("192.0.2.211") : lab.Address;
        using(var client = await BanSession.Connect(lab))
        {
            if(proxy) await client.Send(BanHeader(identity, true));
            await client.Send("{synthetic-secret:!}\n");
            await client.Closed();
        }
        await lab.Server.Empty();
        lab.Server.Bans.Received(banned ? 1 : 0).Ban(identity, TimeSpan.FromMinutes(3));
        if(!banned) lab.Server.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
    }

    [NonLoopbackTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealBanExpiry_RestoresDirectAndNormalizedForwardedClients(bool proxy, bool tls)
    {
        var bans = new IntegratedBanManager();
        await using var lab = new BanLab(proxy, tls, bans: bans);
        // This fixture's tests run serially in their xUnit collection. Keep the
        // forwarded key separate from other classes' static integrated-cache keys.
        var identity = proxy ? IPAddress.Parse("198.18.0.211") : lab.Address;
        bans.Ban(identity, TimeSpan.FromMilliseconds(750));
        Assert.True(bans.IsBanned(identity));
        if(proxy)
        {
            using var banned = await BanSession.Connect(lab);
            await banned.Send(BanHeader(identity, true));
            await banned.Closed();
        }
        else await lab.Server.Rejected();
        await lab.Server.Empty();
        await Until(() => !bans.IsBanned(identity));
        using var recovered = await BanSession.Connect(lab);
        if(proxy) await recovered.Send(BanHeader(identity, true));
        Assert.NotNull(await recovered.Exchange());
    }

    private static string BanHeader(IPAddress address, bool mapped) => mapped
        ? $"PROXY TCP6 {address.MapToIPv6()} ::1 123 3333\r\n" : Header(address.ToString());

    private static async Task AssertAutomaticBanMetric(string pool, string outcome, string reason)
    {
        using var output = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        Assert.Contains(Encoding.UTF8.GetString(output.ToArray()).Split('\n'), line =>
            line.StartsWith("miningcore_stratum_automatic_bans_total{") &&
            line.Contains($"pool=\"{pool}\"") && line.Contains($"outcome=\"{outcome}\"") &&
            line.Contains($"reason=\"{reason}\"") && line.EndsWith(" 1"));
    }

    [NonLoopbackFact]
    public async Task BanDuringOwnedAccounting_DrainsAndAcknowledgesBeforeRejectingNextRequest()
    {
        await using var lab = new BanLab(true, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var share = new Miningcore.Blockchain.Share();
        share.SetPersistenceAdmission(persisted.Task);
        CancellationToken ownedToken = default;
        lab.Server.Handler = async (connection, request, ct) =>
        {
            ownedToken = ct;
            entered.TrySetResult();
            await lab.Server.Account(share, () => connection.RespondAsync(true, request.Value.Id));
        };
        using var client = await BanSession.Connect(lab);
        var identity = IPAddress.Parse("192.0.2.211");
        await client.Send(BanHeader(identity, true) + "{\"id\":1,\"method\":\"mining.submit\"}\n");
        await entered.Task.WaitAsync(Timeout);
        try
        {
            lab.Server.Bans.IsBanned(identity).Returns(true);
            Assert.False(ownedToken.IsCancellationRequested);
            Assert.Equal(1, lab.Server.ConnectionAdmission.Snapshot.Active);
            Assert.Equal(1, lab.Server.TrackedConnectionTaskCount);
        }
        finally { persisted.TrySetResult(); }
        Assert.NotNull(await client.Read());
        Assert.Equal(1, lab.Server.Requests);
        lab.Server.Bus.Received(1).SendMessage(share, Arg.Any<string>());
        await client.Send("{\"id\":2,\"method\":\"ping\"}\n");
        await client.Closed();
        await lab.Server.Empty();
        Assert.Equal(1, lab.Server.Requests);
        Assert.Equal(0, lab.Server.ConnectionAdmission.Snapshot.Active);
    }

    // Use a real non-loopback transport: IntegratedBanManager intentionally exempts
    // loopback, so loopback-only TLS tests cannot prove shared-proxy protection.
    internal static IPAddress AvailableBanTransportAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .Select(x => x.Address)
        .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));

    private static IPAddress BanTransportAddress() => AvailableBanTransportAddress() ??
        throw new InvalidOperationException("Non-loopback test must be skipped without a local IPv4 interface");

    private static async Task ClosedAfterTlsAlert(TcpClient client)
    {
        // SslStream/OpenSSL may emit a TLS alert before aborting a failed handshake.
        // Drain that bounded record before requiring EOF/reset; a byte is not an
        // application response and does not mean this connection survived setup.
        using var deadline = new CancellationTokenSource(Timeout);
        var buffer = new byte[256];
        var total = 0;
        try
        {
            int count;
            while((count = await client.GetStream().ReadAsync(buffer, deadline.Token)) != 0)
            {
                if(total == 0) Assert.Equal(0x15, buffer[0]);
                total += count;
                Assert.InRange(total, 0, buffer.Length);
            }
        }
        catch(IOException ex) when(ex.InnerException is SocketException { SocketErrorCode:
            SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
    }

    private sealed class BanLab : IAsyncDisposable
    {
        internal readonly Server Server;
        internal readonly IPAddress Address = BanTransportAddress();
        internal readonly bool Tls;
        private readonly string certificatePath;

        internal BanLab(bool proxy, bool tls, bool mandatory = true, bool tlsAuto = false,
            bool enableUntrustedProxy = false, IBanManager bans = null, StratumAdmissionConfig config = null,
            ILogger log = null, ClusterConfig cluster = null, PoolEndpoint endpointConfig = null)
        {
            Tls = tls;
            if(tls)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                certificatePath = Path.Combine(Path.GetTempPath(), $"ban-attribution-{Guid.NewGuid():N}.pfx");
                File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
            }
            if(endpointConfig != null)
            {
                endpointConfig.Tls = tls;
                endpointConfig.TlsPfxFile = certificatePath;
            }
            Server = new Server(config ?? new StratumAdmissionConfig(), proxy: proxy || enableUntrustedProxy
                ? new TcpProxyProtocolConfig { Enable = true, Mandatory = mandatory,
                    ProxyAddresses = new[] { proxy ? Address.ToString() : "192.0.2.254" } } : null,
                tlsCertificate: certificatePath, tlsAuto: tlsAuto, bans: bans, listenAddress: Address, log: log,
                cluster: cluster, endpointConfig: endpointConfig);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Server.DisposeAsync(); }
            finally
            {
                if(certificatePath != null)
                {
                    Server.RemoveCertificate(certificatePath);
                    File.Delete(certificatePath);
                }
            }
        }
    }

    private sealed class BanSession : IDisposable
    {
        private TcpClient client;
        private Stream stream;
        private StreamReader reader;

        internal static async Task<BanSession> Connect(BanLab lab, IPAddress source = null)
        {
            var result = new BanSession();
            try
            {
                result.client = await lab.Server.Connect(source: source ?? lab.Address);
                result.stream = result.client.GetStream();
                if(lab.Tls)
                {
                    var ssl = new SslStream(result.stream, false, (_, _, _, _) => true);
                    result.stream = ssl;
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    { TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }).WaitAsync(Timeout);
                }
                result.reader = new StreamReader(result.stream, Encoding.UTF8, false, 1024, true);
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal Task Send(string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
        internal async Task<string> Exchange()
        {
            await Send("{\"id\":1,\"method\":\"ping\"}\n");
            return await Read();
        }
        internal Task<string> Read() => reader.ReadLineAsync().WaitAsync(Timeout);
        internal async Task Closed()
        {
            try { Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout)); }
            catch(IOException ex) when(ex.InnerException is SocketException { SocketErrorCode:
                SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
        }
        public void Dispose()
        {
            reader?.Dispose();
            stream?.Dispose();
            client?.Dispose();
        }
    }
}

public sealed class NonLoopbackTheoryAttribute : TheoryAttribute
{
    public NonLoopbackTheoryAttribute()
    {
        if(StratumAdmissionTests.AvailableBanTransportAddress() == null)
            Skip = "Requires a local non-loopback IPv4 interface for real ban attribution";
    }
}

public sealed class NonLoopbackFactAttribute : FactAttribute
{
    public NonLoopbackFactAttribute()
    {
        if(StratumAdmissionTests.AvailableBanTransportAddress() == null)
            Skip = "Requires a local non-loopback IPv4 interface for real ban attribution";
    }
}
