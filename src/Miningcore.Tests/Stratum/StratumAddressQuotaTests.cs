using System;
using System.Net;
using Miningcore.Configuration;
using Miningcore.Stratum;
using NLog;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumAddressQuotaTests
{
    [Theory]
    [InlineData(64, "2001:db8:1234:5678::")]
    [InlineData(56, "2001:db8:1234:5600::")]
    [InlineData(65, "2001:db8:1234:5678:8000::")]
    [InlineData(128, "2001:db8:1234:5678:abcd::1")]
    [InlineData(0, "::")]
    public void PrefixMaskAndMappedIpv4AreCanonical(int prefix, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), StratumAddressQuota.Key(IPAddress.Parse("2001:db8:1234:5678:abcd::1"), prefix));
        Assert.Equal(IPAddress.Parse("192.0.2.1"), StratumAddressQuota.Key(IPAddress.Parse("::ffff:192.0.2.1"), prefix));
        Assert.Equal(StratumAddressQuota.Key(IPAddress.Parse("fe80::1%2"), prefix),
            StratumAddressQuota.Key(IPAddress.Parse("fe80::1%3"), prefix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedPrefixLimitsDirectAndProxyClientsButKeepsExactIdentity(bool proxy)
    {
        var config = new StratumAdmissionConfig { MaxConcurrentConnectionsPerAddress = 1 };
        var admission = new StratumConnectionAdmission(config, TimeProvider.System, "quota-test", LogManager.CreateNullLogger());
        var first = IPAddress.Parse("2001:db8:1::1");
        var second = IPAddress.Parse("2001:db8:1::2");
        Assert.True(admission.TryAcquire(first, proxy, out var lease));
        using(lease)
        {
            Assert.True(lease.TrySetIdentity(first));
            Assert.False(lease.TrySetIdentity(second));
            if(proxy)
            {
                Assert.True(admission.TryAcquire(IPAddress.Loopback, true, out var pending));
                using(pending) Assert.False(pending.TrySetIdentity(second));
            }
            else Assert.False(admission.TryAcquire(second, false, out _));
            Assert.True(admission.TryAcquire(IPAddress.Parse("2001:db8:2::1"), false, out var other));
            other.Dispose();
        }
        Assert.True(admission.TryAcquire(second, false, out var recovered));
        recovered.Dispose(); admission.Stop();
        Assert.Equal((0, 0), admission.Snapshot);
        // Quota grouping must never broaden a trusted proxy list.
        var trust = new StratumProxyPolicy(new TcpProxyProtocolConfig {Enable = true, ProxyAddresses = new[] {first.ToString()}});
        Assert.True(trust.IsTrustedPeer(first)); Assert.False(trust.IsTrustedPeer(second));
    }

    [Fact]
    public void PrefixIsConfigurableAndValidated()
    {
        var validator = new StratumAdmissionConfigValidator();
        Assert.False(validator.Validate(new StratumAdmissionConfig {Ipv6PrefixLength = -1}).IsValid);
        Assert.False(validator.Validate(new StratumAdmissionConfig {Ipv6PrefixLength = 129}).IsValid);
        var admission = new StratumConnectionAdmission(new StratumAdmissionConfig
            {Ipv6PrefixLength = 128, MaxConcurrentConnectionsPerAddress = 1}, TimeProvider.System, "quota-exact", LogManager.CreateNullLogger());
        Assert.True(admission.TryAcquire(IPAddress.Parse("2001:db8::1"), false, out var first));
        Assert.True(admission.TryAcquire(IPAddress.Parse("2001:db8::2"), false, out var second));
        first.Dispose(); second.Dispose(); admission.Stop();
    }
}
