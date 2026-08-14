using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumListenerReservationCoordinatorTests
{
    [Fact]
    public void ReservedSocket_IsBoundButDoesNotListenDuringPoolInitialization()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pool = CreatePool("pool-a", port, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();

        using var session = coordinator.ReserveAll(new[] { pool });
        var reservation = Assert.Single(session.Claim(pool.Id));

        try
        {
            Assert.True(reservation.Socket.IsBound);
            Assert.Throws<InvalidOperationException>(() =>
                reservation.Socket.Accept());
        }
        finally
        {
            reservation.Dispose();
        }
    }

    [Fact]
    public void DistinctSpecificAddresses_CanReserveOneNumericPort()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pools = new[]
        {
            CreatePool("pool-a", port, "127.0.0.1"),
            CreatePool("pool-b", port, "127.0.0.2"),
        };
        var coordinator = new StratumListenerReservationCoordinator();

        using var session = coordinator.ReserveAll(pools);
        var first = Assert.Single(session.Claim("pool-a"));
        var second = Assert.Single(session.Claim("pool-b"));

        try
        {
            Assert.True(first.Socket.IsBound);
            Assert.True(second.Socket.IsBound);
            Assert.Equal(port,
                ((IPEndPoint) first.Socket.LocalEndPoint).Port);
            Assert.Equal(port,
                ((IPEndPoint) second.Socket.LocalEndPoint).Port);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void ReservationFailure_DisposesEverySocketAcquiredByAttempt()
    {
        var firstPort = GetFreePort(IPAddress.Loopback);
        var secondPort = GetFreePort(IPAddress.Loopback);
        var calls = 0;
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
        {
            calls++;
            if(calls == 2)
            {
                throw new SocketException(
                    (int) SocketError.AddressAlreadyInUse);
            }

            return StratumServer.CreateBoundSocket(endpoint);
        });
        var pools = new[]
        {
            CreatePool("pool-a", firstPort, "127.0.0.1"),
            CreatePool("pool-b", secondPort, "127.0.0.1"),
        };

        var error = Assert.Throws<PoolStartupException>(() =>
            coordinator.ReserveAll(pools));

        Assert.Equal("pool-b", error.PoolId);
        Assert.Contains("127.0.0.1", error.Message,
            StringComparison.Ordinal);
        Assert.Contains(secondPort.ToString(), error.Message,
            StringComparison.Ordinal);
        Assert.Contains(SocketError.AddressAlreadyInUse.ToString(),
            error.Message, StringComparison.Ordinal);
        Assert.Contains("native error", error.Message,
            StringComparison.Ordinal);

        using var reacquired = StratumServer.CreateBoundSocket(
            new IPEndPoint(IPAddress.Loopback, firstPort));
    }

    [LinuxFact]
    public void EndpointOccupiedByAnotherProcess_FailsWithSocketClassification()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();

        try
        {
            var port = ((IPEndPoint) occupied.LocalEndpoint).Port;
            var pool = CreatePool("occupied", port, "127.0.0.1");
            var coordinator = new StratumListenerReservationCoordinator();

            var error = Assert.Throws<PoolStartupException>(() =>
                coordinator.ReserveAll(new[] { pool }));

            Assert.Equal(pool.Id, error.PoolId);
            Assert.Contains($"127.0.0.1:{port}", error.Message,
                StringComparison.Ordinal);
            Assert.Contains(SocketError.AddressAlreadyInUse.ToString(),
                error.Message, StringComparison.Ordinal);
        }
        finally
        {
            occupied.Stop();
        }
    }

    [Fact]
    public void ClaimedListener_IsReleasedForImmediateRestart()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pool = CreatePool("pool-a", port, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();

        using(var firstSession = coordinator.ReserveAll(new[] { pool }))
        {
            var first = Assert.Single(firstSession.Claim(pool.Id));
            first.Dispose();
        }

        using var restartedSession = coordinator.ReserveAll(new[] { pool });
        var restarted = Assert.Single(restartedSession.Claim(pool.Id));
        restarted.Dispose();
    }

    [Fact]
    public void UnclaimedSessionDispose_ReleasesListenerForRetry()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pool = CreatePool("pool-a", port, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();

        var failedStartupSession = coordinator.ReserveAll(new[] { pool });
        failedStartupSession.Dispose();

        using var retrySession = coordinator.ReserveAll(new[] { pool });
        var retry = Assert.Single(retrySession.Claim(pool.Id));
        retry.Dispose();
    }

    [Fact]
    public void ClaimingOnePoolTwice_FailsInsteadOfSharingSocketOwnership()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pool = CreatePool("pool-a", port, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();

        using var session = coordinator.ReserveAll(new[] { pool });
        var listener = Assert.Single(session.Claim(pool.Id));

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                session.Claim(pool.Id));
            Assert.Contains("already claimed", error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [LinuxFact]
    public void NonLocalSpecificAddress_ReportsEndpointAndSocketClassification()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pool = CreatePool("non-local", port, "192.0.2.1");
        var coordinator = new StratumListenerReservationCoordinator();

        var error = Assert.Throws<PoolStartupException>(() =>
            coordinator.ReserveAll(new[] { pool }));

        Assert.Equal(pool.Id, error.PoolId);
        Assert.Contains($"192.0.2.1:{port}", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("socket error", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("native error", error.Message,
            StringComparison.Ordinal);
    }

    private static int GetFreePort(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static PoolConfig CreatePool(string id, int port,
        string listenAddress) => new()
        {
            Id = id,
            Enabled = true,
            EnableInternalStratum = true,
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
