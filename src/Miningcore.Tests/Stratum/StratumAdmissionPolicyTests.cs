using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Stratum;
using NLog;
using Xunit;

namespace Miningcore.Tests.Stratum;

public partial class StratumAdmissionTests
{
    [Fact]
    public void ProxyPolicy_FreezesLargeNormalizedTrustSetAndFlags()
    {
        var addresses = Enumerable.Range(0, 10000)
            .Select(i => $"192.0.{i >> 8}.{i & 255}").ToArray();
        addresses[0] = "::ffff:127.0.0.1";
        var config = new TcpProxyProtocolConfig { Enable = true, Mandatory = true, ProxyAddresses = addresses };
        var policy = new StratumProxyPolicy(config);
        Array.Fill(addresses, "invalid-after-startup");
        config.ProxyAddresses = new[] { "203.0.113.1" };
        config.Enable = config.Mandatory = false;
        Assert.True(policy.Enabled);
        Assert.True(policy.Mandatory);
        Assert.True(policy.IsTrustedPeer(IPAddress.Loopback));
        Assert.True(policy.IsTrustedPeer(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.True(policy.IsTrustedPeer(IPAddress.Parse("192.0.39.15")));
        Assert.False(policy.IsTrustedPeer(IPAddress.Parse("203.0.113.1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProxyPolicy_EmptyOrOmittedTrustList_UsesOnlyLoopback(bool empty)
    {
        var policy = new StratumProxyPolicy(new TcpProxyProtocolConfig
            { Enable = true, ProxyAddresses = empty ? Array.Empty<string>() : null });
        Assert.True(policy.IsTrustedPeer(IPAddress.Loopback));
        Assert.True(policy.IsTrustedPeer(IPAddress.IPv6Loopback));
        Assert.True(policy.IsTrustedPeer(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(policy.IsTrustedPeer(IPAddress.Parse("127.0.0.2")));
        Assert.False(policy.IsTrustedPeer(IPAddress.Parse("192.0.2.1")));
        Assert.False(new StratumProxyPolicy(null).IsTrustedPeer(IPAddress.Loopback));
        Assert.False(new StratumProxyPolicy(new TcpProxyProtocolConfig
            { ProxyAddresses = new[] { "invalid" } }).Enabled);
        Assert.Throws<InvalidOperationException>(() => new StratumProxyPolicy(new TcpProxyProtocolConfig
            { Enable = true, ProxyAddresses = new[] { "invalid" } }));
    }

    [Fact]
    public async Task ProxyPolicy_AcceptAndHeaderUseSameSnapshotDespiteConfigurationMutation()
    {
        var proxy = new TcpProxyProtocolConfig
            { Enable = true, Mandatory = true, ProxyAddresses = new[] { "::ffff:127.0.0.1" } };
        await using var server = new Server(new StratumAdmissionConfig { MaxPendingIdentities = 1 }, proxy: proxy);
        proxy.ProxyAddresses[0] = "invalid-after-startup";
        proxy.Enable = proxy.Mandatory = false;
        using(var pending = await server.Connect())
        {
            await Until(() => server.Accepted == 1);
            await server.Rejected(); // Still classified as trusted and charged a pending slot.
            await Send(pending, Header("192.0.2.1"), false);
            var response = await Exchange(pending);
            Assert.Contains("192.0.2.1", response); // Header uses the same frozen trust set.
        }
        await server.Empty();
        await server.Rejected(header: ""); // Mandatory flag is also frozen.
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task ProxyPolicy_MutationCannotTurnAnUntrustedPeerIntoATrustedProxy()
    {
        var proxy = new TcpProxyProtocolConfig
            { Enable = true, Mandatory = true, ProxyAddresses = new[] { "192.0.2.1" } };
        await using var server = new Server(new StratumAdmissionConfig(), proxy: proxy);
        proxy.ProxyAddresses[0] = "127.0.0.1";
        await server.Rejected(header: Header("203.0.113.1"));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task InvalidProxyPolicy_ReleasesAllListenerReservations()
    {
        var server = new Server(new StratumAdmissionConfig(), ports: 2,
            proxy: new TcpProxyProtocolConfig { Enable = true, ProxyAddresses = new[] { "invalid" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.Run);
        foreach(var endpoint in server.Endpoints)
        {
            using var rebound = StratumServer.CreateBoundSocket(endpoint);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.DisposeAsync());
    }

    [Fact]
    public async Task RejectedSecondRun_PreservesReusedReservationsAndDisposesOnlyNewOnes()
    {
        await using var server = new Server(new StratumAdmissionConfig(), ports: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.RunAsync(CancellationToken.None, server.Reservations));
        using var socket = StratumServer.CreateBoundSocket(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = new StratumEndpoint((IPEndPoint) socket.LocalEndPoint, new PoolEndpoint());
        using var fresh = new StratumListenerReservation(server.PoolId, endpoint, socket);
        fresh.Activate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.RunAsync(CancellationToken.None, server.Reservations[0], fresh));
        using var rebound = StratumServer.CreateBoundSocket(endpoint.IPEndPoint);
        await server.Exchange(0);
        await server.Exchange(1);
    }

    [Fact]
    public async Task StoppedAdmission_CountsTransportAndPendingIdentityRefusalsWithoutChangingOccupancy()
    {
        var id = Guid.NewGuid().ToString("N");
        var admission = new StratumConnectionAdmission(new StratumAdmissionConfig(),
            new ManualTimeProvider(), id, LogManager.CreateNullLogger());
        Assert.True(admission.TryAcquire(IPAddress.Loopback, true, out var pending));
        admission.Stop();
        for(var i = 0; i < 3; i++)
            Assert.False(admission.TryAcquire(IPAddress.Loopback, false, out _));
        Assert.False(pending.TrySetIdentity(IPAddress.Parse("192.0.2.1")));
        Assert.Equal((1, 0), admission.Snapshot);
        pending.Dispose();
        Assert.False(pending.TrySetIdentity(IPAddress.Loopback)); // Already-disposed misuse is not a new refusal.
        Assert.Equal((0, 0), admission.Snapshot);
        Assert.Contains(await Series(id), x => x.Contains("reason=\"stopped\"") && x.EndsWith(" 4"));
    }

    [Fact]
    public async Task AdvisoryWarnings_CannotSuppressRefusals_AndBothBudgetsRemainBounded()
    {
        using var logs = new LogFactory();
        var target = new NLog.Targets.MemoryTarget { Layout = "${message}" };
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
        logs.Configuration = logging;
        var time = new ManualTimeProvider();
        await using var server = new Server(new StratumAdmissionConfig
            { MaxConcurrentConnectionsPerAddress = 2, BurstPerAddress = 100 }, time, log: logs.GetLogger("budgets"));
        using var first = await server.Connect();
        Assert.NotNull(await Exchange(first));
        for(var i = 0; i < 3; i++)
        {
            using(var second = await server.Connect())
            {
                Assert.NotNull(await Exchange(second));
                await server.Rejected();
            }
            await Until(() => server.ConnectionAdmission.Snapshot.Active == 1);
        }
        Assert.Equal(2, target.Logs.Count);
        Assert.Contains(target.Logs, x => x.Contains("address-near-capacity"));
        Assert.Contains(target.Logs, x => x.Contains("address-concurrency"));
        time.MoveWallClock(TimeSpan.FromDays(100));
        time.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        using(var second = await server.Connect())
        {
            Assert.NotNull(await Exchange(second));
            await server.Rejected();
        }
        Assert.Equal(4, target.Logs.Count);
        Assert.All(target.Logs.Skip(2), x => Assert.Contains("suppressed since last category summary: 2", x));
        Assert.DoesNotContain(target.Logs, x => x.Contains("127.0.0.1"));
    }

    [Fact]
    public async Task AddressCapacity_DefensiveFullLedgerRefusesNewIdentitiesAndPreservesExisting()
    {
        var config = new StratumAdmissionConfig
        {
            MaxConcurrentConnections = 2, Burst = 2, ConnectionsPerSecond = 1,
            IdleExpirySeconds = 1, BurstPerAddress = 1, MaxTrackedAddresses = 5,
        };
        var id = Guid.NewGuid().ToString("N");
        var time = new ManualTimeProvider();
        var admission = new StratumConnectionAdmission(config, time, id, LogManager.CreateNullLogger());
        // Sizing validation conservatively keeps normal traffic below this defensive
        // ceiling. Seed a structurally valid full idle ledger ONLY in the test, rather
        // than weaken validation or expose a production bypass to manufacture history.
        var addresses = (Dictionary<IPAddress, StratumConnectionAdmission.AddressState>) typeof(StratumConnectionAdmission)
            .GetField("addresses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(admission);
        var idle = (LinkedList<StratumConnectionAdmission.AddressState>) typeof(StratumConnectionAdmission)
            .GetField("idle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(admission);
        foreach(var n in Enumerable.Range(1, 5))
        {
            var address = IPAddress.Parse($"192.0.2.{n}");
            var state = new StratumConnectionAdmission.AddressState(address, 1, time.GetTimestamp());
            state.IdleNode = idle.AddLast(state);
            addresses.Add(address, state);
        }
        Assert.False(admission.TryAcquire(IPAddress.Parse("192.0.2.6"), false, out _));
        Assert.Equal((0, 5), admission.Snapshot);
        Assert.True(admission.TryAcquire(IPAddress.Loopback, true, out var pending));
        Assert.False(pending.TrySetIdentity(IPAddress.Parse("192.0.2.6")));
        Assert.Equal((1, 5), admission.Snapshot); // Failed identity keeps its pending lease.
        Assert.Contains(await Series(id), x => x.Contains("reason=\"address-capacity\"") && x.EndsWith(" 2"));
        // Refusal did not consume the pool token or evict an existing entry.
        Assert.True(admission.TryAcquire(IPAddress.Parse("192.0.2.1"), false, out var existing));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        admission.SweepIdle();
        Assert.Equal((2, 1), admission.Snapshot); // Active identity cannot expire; pending transport still owns capacity.
        pending.Dispose();
        Assert.True(admission.TryAcquire(IPAddress.Parse("192.0.2.6"), false, out var recovered));
        existing.Dispose();
        recovered.Dispose();
        admission.Stop();
        Assert.Equal((0, 0), admission.Snapshot);
    }
}
