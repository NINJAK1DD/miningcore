using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Stratum;
using Xunit;

namespace Miningcore.Tests;

public class StratumListenerConfigurationTests
{
    [Theory]
    [InlineData("127.0.0.1", 3032, "127.0.0.2", 3032, false)]
    [InlineData("2001:db8::1", 3032, "2001:db8::2", 3032, false)]
    [InlineData("127.0.0.1", 3032, "127.0.0.1", 3032, true)]
    [InlineData("::1", 3032, "::1", 3032, true)]
    [InlineData("*", 3032, "127.0.0.2", 3032, true)]
    [InlineData(null, 3032, "127.0.0.1", 3032, true)]
    [InlineData("0.0.0.0", 3032, "::1", 3032, false)]
    [InlineData("::", 3032, "127.0.0.2", 3032, true)]
    [InlineData("::", 3032, "2001:db8::2", 3032, true)]
    [InlineData("127.0.0.1", 3032, "::ffff:127.0.0.1", 3032, true)]
    [InlineData("127.0.0.1", 3032, "127.0.0.1", 3033, false)]
    public void ConflictDetection_UsesEffectiveAddressAndPort(
        string firstAddress, int firstPort, string secondAddress,
        int secondPort, bool expectedConflict)
    {
        var pools = new[]
        {
            CreatePool("pool-a", firstPort, firstAddress),
            CreatePool("pool-b", secondPort, secondAddress),
        };

        var conflict = ClusterConfigValidator
            .FindStratumListenerConflict(pools);

        Assert.Equal(expectedConflict, conflict != null);
    }

    [Fact]
    public void OverlapComparison_IsSymmetricForWildcardAndMappedAddresses()
    {
        var pairs = new[]
        {
            ("0.0.0.0", "127.0.0.2"),
            ("0.0.0.0", "::1"),
            ("::", "127.0.0.2"),
            ("::", "2001:db8::2"),
            ("127.0.0.1", "::ffff:127.0.0.1"),
        };

        foreach(var (firstValue, secondValue) in pairs)
        {
            Assert.True(ListenerAddressUtils.TryResolve(firstValue,
                out var first));
            Assert.True(ListenerAddressUtils.TryResolve(secondValue,
                out var second));
            Assert.Equal(ListenerAddressUtils.Overlaps(first, second),
                ListenerAddressUtils.Overlaps(second, first));
        }
    }

    [Fact]
    public void ConflictingListeners_ReportBothPoolAndEndpointIdentities()
    {
        var config = CreateCluster(
            CreatePool("pool-a", 3032, "*"),
            CreatePool("pool-b", 3032, "127.0.0.2"));

        var result = new ClusterConfigValidator().Validate(config);

        var error = Assert.Single(result.Errors, failure =>
            failure.ErrorMessage.StartsWith("Stratum listener conflict:"));
        Assert.Equal(
            "Stratum listener conflict: pool 'pool-a' endpoint 0.0.0.0:3032 overlaps pool 'pool-b' endpoint 127.0.0.2:3032",
            error.ErrorMessage);
    }

    [Fact]
    public void DistinctSpecificAddresses_MayReusePort()
    {
        var config = CreateCluster(
            CreatePool("pool-a", 3032, "127.0.0.1"),
            CreatePool("pool-b", 3032, "127.0.0.2"));

        var result = new ClusterConfigValidator().Validate(config);

        Assert.DoesNotContain(result.Errors, failure =>
            failure.ErrorMessage.StartsWith("Stratum listener conflict:"));
    }

    [Fact]
    public void RecoveryMode_SkipsStratumListenerConflictValidation()
    {
        var config = CreateCluster(
            CreatePool("pool-a", 3032, "*"),
            CreatePool("pool-b", 3032, "127.0.0.2"));

        var result = new ClusterConfigValidator(true).Validate(config);

        Assert.DoesNotContain(result.Errors, failure =>
            failure.ErrorMessage.StartsWith("Stratum listener conflict:"));
    }

    [Fact]
    public void DistinctSpecificAddresses_MatchRealLinuxSocketBinding()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var firstAddress = IPAddress.Parse("127.0.0.1");
        var secondAddress = IPAddress.Parse("127.0.0.2");
        Assert.False(ListenerAddressUtils.Overlaps(firstAddress,
            secondAddress));

        using var first = BindAndListen(firstAddress, 0);
        var port = ((IPEndPoint) first.LocalEndPoint).Port;
        using var second = BindAndListen(secondAddress, port);

        Assert.Equal(port, ((IPEndPoint) second.LocalEndPoint).Port);
    }

    private static Socket BindAndListen(IPAddress address, int port)
    {
        var endpoint = new IPEndPoint(address, port);
        var socket = StratumServer.CreateListenSocket(endpoint);
        socket.SetSocketOption(SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress, true);
        socket.Bind(endpoint);
        socket.Listen();
        return socket;
    }

    private static ClusterConfig CreateCluster(params PoolConfig[] pools) =>
        new()
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
            Pools = pools,
        };

    private static PoolConfig CreatePool(string id, int port,
        string listenAddress) => new()
    {
        Id = id,
        Coin = "bitcoin",
        Enabled = true,
        EnableInternalStratum = true,
        Address = $"{id}-wallet",
        Daemons = new[]
        {
            new DaemonEndpointConfig
            {
                Host = "127.0.0.1",
                Port = 8332,
            },
        },
        Ports = new Dictionary<int, PoolEndpoint>
        {
            [port] = new()
            {
                Difficulty = 1,
                ListenAddress = listenAddress,
            },
        },
    };
}
