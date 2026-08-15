using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Miningcore.Stratum;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumListenerPortabilityTests
{
    [Fact]
    public void NativeBindFallbackCandidates_ResolveCurrentUnixPlatform()
    {
        if(OperatingSystem.IsWindows())
            return;

        var candidate = StratumServer.ProbeNativeBindLibraryCandidates();

        Assert.False(string.IsNullOrWhiteSpace(candidate));
    }

    [Fact]
    public void LinuxNativeBindFallbackCandidates_CoverSupportedArchitectures()
    {
        var expectedAliases = new Dictionary<Architecture, string[]>
        {
            [Architecture.X64] = new[] { "x86_64" },
            [Architecture.X86] = new[] { "x86", "i386" },
            [Architecture.Arm] = new[] { "armhf", "armv7" },
            [Architecture.Arm64] = new[] { "aarch64" },
            [Architecture.S390x] = new[] { "s390x" },
            [Architecture.Ppc64le] = new[]
            {
                "powerpc64le",
                "ppc64le",
            },
            [Architecture.RiscV64] = new[] { "riscv64" },
        };

        foreach(var (architecture, aliases) in expectedAliases)
        {
            var candidates = StratumServer
                .GetLinuxNativeBindLibraryCandidates(architecture);

            Assert.Equal("libc.so.6", candidates[0]);

            foreach(var alias in aliases)
            {
                Assert.Contains($"libc.musl-{alias}.so.1", candidates);
                Assert.Contains($"/lib/libc.musl-{alias}.so.1", candidates);
                Assert.Contains($"ld-musl-{alias}.so.1", candidates);
                Assert.Contains($"/lib/ld-musl-{alias}.so.1", candidates);
            }
        }
    }

    [Fact]
    public void NativeExclusiveReservation_BlocksCompetitorAndAllowsImmediateRebind()
    {
        var first = StratumServer.CreateBoundSocket(
            new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint) first.LocalEndPoint;

        try
        {
            var error = Assert.Throws<SocketException>(() =>
            {
                using var competing = StratumServer.CreateBoundSocket(endpoint);
            });

            AssertExclusiveConflict(error);
        }
        finally
        {
            first.Dispose();
        }

        using var restarted = StratumServer.CreateBoundSocket(endpoint);
        Assert.True(restarted.IsBound);
        Assert.Equal(endpoint, restarted.LocalEndPoint);
    }

    [Fact]
    public void NativeDualStackReservation_BlocksIPv4CompetitorAndAllowsImmediateRebind()
    {
        if(!Socket.OSSupportsIPv6)
            return;

        Socket first;

        try
        {
            first = StratumServer.CreateBoundSocket(
                new IPEndPoint(IPAddress.IPv6Any, 0));
        }
        catch(SocketException ex) when(ex.SocketErrorCode is
                  SocketError.AddressFamilyNotSupported or
                  SocketError.ProtocolFamilyNotSupported)
        {
            return;
        }

        var endpoint = (IPEndPoint) first.LocalEndPoint;

        try
        {
            var error = Assert.Throws<SocketException>(() =>
            {
                using var competing = StratumServer.CreateBoundSocket(
                    new IPEndPoint(IPAddress.Loopback, endpoint.Port));
            });

            AssertExclusiveConflict(error);
        }
        finally
        {
            first.Dispose();
        }

        using var restarted = StratumServer.CreateBoundSocket(endpoint);
        Assert.True(restarted.IsBound);
        Assert.Equal(endpoint, restarted.LocalEndPoint);
    }

    private static void AssertExclusiveConflict(SocketException error)
    {
        if(OperatingSystem.IsWindows())
        {
            // Windows may classify an IPv4/dual-stack exclusivity collision as access denied.
            Assert.Contains(error.SocketErrorCode, new[]
            {
                SocketError.AddressAlreadyInUse,
                SocketError.AccessDenied,
            });
        }
        else
        {
            Assert.Equal(SocketError.AddressAlreadyInUse,
                error.SocketErrorCode);
        }
    }
}
