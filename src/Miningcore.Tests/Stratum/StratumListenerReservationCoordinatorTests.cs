using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumListenerReservationCoordinatorTests
{
    [Fact]
    public async Task ReservedSocket_IsBoundButDoesNotListenDuringPoolInitialization()
    {
        var pool = CreatePool("pool-a", 0, "127.0.0.1");
        var coordinator = CreateCoordinatorWithoutRetry();

        using var session = await coordinator.ReserveAllAsync(new[] { pool });
        var reservation = Assert.Single(session.Claim(pool.Id));

        try
        {
            Assert.True(reservation.Socket.IsBound);
            Assert.Equal(0, (int) reservation.Socket.GetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress));
            Assert.Throws<InvalidOperationException>(() =>
                reservation.Socket.Accept());

            reservation.Activate();
            using var client = new TcpClient(AddressFamily.InterNetwork);
            client.Connect((IPEndPoint) reservation.Socket.LocalEndPoint);
            using var accepted = reservation.Socket.Accept();
        }
        finally
        {
            reservation.Dispose();
        }
    }

    [Fact]
    public async Task CompetingBoundReservation_FailsBeforeEitherSocketListens()
    {
        var pool = CreatePool("pool-a", 0, "127.0.0.1");
        var coordinator = CreateCoordinatorWithoutRetry();

        using var firstSession = await coordinator.ReserveAllAsync(
            new[] { pool });
        var firstReservation = Assert.Single(firstSession.Claim(pool.Id));

        try
        {
            Assert.True(firstReservation.Socket.IsBound);
            var boundPort = ((IPEndPoint)
                firstReservation.Socket.LocalEndPoint).Port;
            var competingPool = CreatePool(pool.Id, boundPort,
                "127.0.0.1");

            var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
                coordinator.ReserveAllAsync(new[] { competingPool }));

            Assert.Equal(pool.Id, error.PoolId);
            Assert.Contains(SocketError.AddressAlreadyInUse.ToString(),
                error.Message, StringComparison.Ordinal);
        }
        finally
        {
            firstReservation.Dispose();
        }
    }

    [Fact]
    public async Task BoundReservation_SurvivesForcedFinalizationAndRemainsExclusive()
    {
        var pool = CreatePool("pool-a", 0, "127.0.0.1");
        var coordinator = CreateCoordinatorWithoutRetry();

        using var session = await coordinator.ReserveAllAsync(new[] { pool });
        var reservation = Assert.Single(session.Claim(pool.Id));

        try
        {
            var endpoint = (IPEndPoint) reservation.Socket.LocalEndPoint;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.True(reservation.Socket.IsBound);
            var competingPool = CreatePool("pool-b", endpoint.Port,
                endpoint.Address.ToString());
            var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
                coordinator.ReserveAllAsync(new[] { competingPool }));
            Assert.Contains(SocketError.AddressAlreadyInUse.ToString(),
                error.Message, StringComparison.Ordinal);

            reservation.Activate();
            Assert.True(reservation.IsActivated);
        }
        finally
        {
            reservation.Dispose();
        }
    }

    [Fact]
    public async Task DistinctSpecificAddresses_CanReserveOneNumericPort()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pools = new[]
        {
            CreatePool("pool-a", port, "127.0.0.1"),
            CreatePool("pool-b", port, "127.0.0.2"),
        };
        var coordinator = new StratumListenerReservationCoordinator();

        using var session = await coordinator.ReserveAllAsync(pools);
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
    public async Task ReservationFailure_DisposesEverySocketAcquiredByAttempt()
    {
        var firstPort = GetFreePort(IPAddress.Loopback);
        int secondPort;

        do
        {
            secondPort = GetFreePort(IPAddress.Loopback);
        } while(secondPort == firstPort);
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
            }, addressInUseRetryWindow: TimeSpan.Zero);
        var pools = new[]
        {
            CreatePool("pool-a", firstPort, "127.0.0.1"),
            CreatePool("pool-b", secondPort, "127.0.0.1"),
        };

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(pools));

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

    [Fact]
    public async Task DuplicatePoolId_FailsAndReleasesEarlierReservations()
    {
        var firstPort = GetFreePort(IPAddress.Loopback);
        var pools = new[]
        {
            CreatePool("duplicate", firstPort, "127.0.0.1"),
            CreatePool("duplicate", 0, "127.0.0.1"),
        };
        var coordinator = CreateCoordinatorWithoutRetry();

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(pools));

        Assert.Equal("duplicate", error.PoolId);
        Assert.Contains("duplicate pool id 'duplicate'", error.Message,
            StringComparison.Ordinal);
        using var reacquired = StratumServer.CreateBoundSocket(
            new IPEndPoint(IPAddress.Loopback, firstPort));
    }

    [LinuxFact]
    public async Task EndpointOccupiedByAnotherProcess_FailsWithSocketClassification()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();

        try
        {
            var port = ((IPEndPoint) occupied.LocalEndpoint).Port;
            var pool = CreatePool("occupied", port, "127.0.0.1");
            var coordinator = CreateCoordinatorWithoutRetry();

            var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
                coordinator.ReserveAllAsync(new[] { pool }));

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
    public async Task ClaimedListener_IsReleasedForImmediateRestart()
    {
        var pool = CreatePool("pool-a", 0, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();
        int port;

        using(var firstSession = await coordinator.ReserveAllAsync(
                  new[] { pool }))
        {
            var first = Assert.Single(firstSession.Claim(pool.Id));
            port = ((IPEndPoint) first.Socket.LocalEndPoint).Port;
            first.Dispose();
        }

        var restartedPool = CreatePool(pool.Id, port, "127.0.0.1");
        using var restartedSession = await coordinator.ReserveAllAsync(
            new[] { restartedPool });
        var restarted = Assert.Single(restartedSession.Claim(pool.Id));
        restarted.Dispose();
    }

    [Fact]
    public async Task UnclaimedSessionDispose_ReleasesListenerForRetry()
    {
        var port = GetFreePort(IPAddress.Loopback);
        var pool = CreatePool("pool-a", port, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();

        var failedStartupSession = await coordinator.ReserveAllAsync(
            new[] { pool });
        failedStartupSession.Dispose();

        using var retrySession = await coordinator.ReserveAllAsync(
            new[] { pool });
        var retry = Assert.Single(retrySession.Claim(pool.Id));
        retry.Dispose();
    }

    [Fact]
    public async Task ClaimingOnePoolTwice_FailsInsteadOfSharingSocketOwnership()
    {
        var pool = CreatePool("pool-a", 0, "127.0.0.1");
        var coordinator = new StratumListenerReservationCoordinator();

        using var session = await coordinator.ReserveAllAsync(new[] { pool });
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

    [Fact]
    public async Task LaterNullEndpointInSamePool_FailsBeforeSocketReservation()
    {
        var reservationAttempts = 0;
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
        {
            reservationAttempts++;
            return StratumServer.CreateBoundSocket(endpoint);
        });
        var pool = CreatePool("null-endpoint", 3031, "127.0.0.1");
        pool.Ports[3032] = null;

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { pool }));

        Assert.Equal(pool.Id, error.PoolId);
        Assert.Equal(0, reservationAttempts);
        Assert.Equal(
            "Pool 'null-endpoint' Stratum port 3032: endpoint configuration must not be null",
            error.Message);
    }

    [Fact]
    public async Task NullEndpointInLaterPool_FailsBeforeSocketReservation()
    {
        var reservationAttempts = 0;
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
        {
            reservationAttempts++;
            return StratumServer.CreateBoundSocket(endpoint);
        });
        var validPool = CreatePool("valid-pool", 3031, "127.0.0.1");
        var nullPool = CreatePool("null-pool", 3032, "127.0.0.1");
        nullPool.Ports[3032] = null;

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { validPool, nullPool }));

        Assert.Equal(nullPool.Id, error.PoolId);
        Assert.Equal(0, reservationAttempts);
        Assert.Equal(
            "Pool 'null-pool' Stratum port 3032: endpoint configuration must not be null",
            error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("239.255.0.1")]
    public async Task InvalidEndpointInLaterPool_FailsBeforeSocketReservation(
        string listenAddress)
    {
        var reservationAttempts = 0;
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
        {
            reservationAttempts++;
            return StratumServer.CreateBoundSocket(endpoint);
        });
        var validPool = CreatePool("valid-pool", 3031, "127.0.0.1");
        var invalidPool = CreatePool("invalid-pool", 3032, listenAddress);

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { validPool, invalidPool }));

        Assert.Equal(invalidPool.Id, error.PoolId);
        Assert.Equal(0, reservationAttempts);
        Assert.Contains("3032", error.Message, StringComparison.Ordinal);
    }

    [LinuxFact]
    public async Task NonLocalSpecificAddress_ReportsEndpointAndSocketClassification()
    {
        const int port = 0;
        var pool = CreatePool("non-local", port, "192.0.2.1");
        var coordinator = new StratumListenerReservationCoordinator();

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { pool }));

        Assert.Equal(pool.Id, error.PoolId);
        Assert.Contains($"192.0.2.1:{port}", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("socket error", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("native error", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddressInUse_IsRetriedWithBoundedExponentialBackoff()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
            {
                attempts++;
                if(attempts < 3)
                {
                    throw new SocketException(
                        (int) SocketError.AddressAlreadyInUse);
                }

                return StratumServer.CreateBoundSocket(endpoint);
            }, addressInUseRetryWindow: TimeSpan.FromSeconds(1),
            retryWait: (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });
        var pool = CreatePool("time-wait", 0, "127.0.0.1");

        using var session = await coordinator.ReserveAllAsync(new[] { pool });
        var reservation = Assert.Single(session.Claim(pool.Id));

        try
        {
            Assert.Equal(3, attempts);
            Assert.Equal(new[]
            {
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(500),
            }, waits);
        }
        finally
        {
            reservation.Dispose();
        }
    }

    [Fact]
    public async Task AddressInUse_AfterRetryDelayBudgetFailsWithPoolScopedDiagnostic()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var coordinator = new StratumListenerReservationCoordinator(_ =>
            {
                attempts++;
                throw new SocketException(
                    (int) SocketError.AddressAlreadyInUse);
            }, addressInUseRetryWindow: TimeSpan.FromMilliseconds(750),
            retryWait: (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });
        var pool = CreatePool("occupied", 3032, "127.0.0.1");

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { pool }));

        Assert.Equal(pool.Id, error.PoolId);
        Assert.Equal(3, attempts);
        Assert.Equal(TimeSpan.FromMilliseconds(750), waits.Aggregate(
            TimeSpan.Zero, (total, delay) => total + delay));
        Assert.Contains(
            "after exhausting the shared 0.75-second startup retry-delay budget",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(SocketError.AddressAlreadyInUse.ToString(),
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessDenied_FailsWithoutConsumingTransientRetryDelayBudget()
    {
        var attempts = 0;
        var waits = 0;
        var coordinator = new StratumListenerReservationCoordinator(_ =>
            {
                attempts++;
                throw new SocketException((int) SocketError.AccessDenied);
            }, addressInUseRetryWindow: TimeSpan.FromSeconds(90),
            retryWait: (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            });
        var pool = CreatePool("exclusive-owner", 3032, "127.0.0.1");

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { pool }));

        Assert.Equal(pool.Id, error.PoolId);
        Assert.Equal(1, attempts);
        Assert.Equal(0, waits);
        Assert.Contains(SocketError.AccessDenied.ToString(), error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("retry-delay budget", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddressInUse_RetryDelayBudgetIsSharedAcrossEveryEndpoint()
    {
        var firstPort = GetFreePort(IPAddress.Loopback);
        int secondPort;

        do
        {
            secondPort = GetFreePort(IPAddress.Loopback);
        } while(secondPort == firstPort);
        var attempts = new Dictionary<int, int>();
        var waits = new List<TimeSpan>();
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
            {
                attempts.TryGetValue(endpoint.Port, out var attempt);
                attempts[endpoint.Port] = ++attempt;

                if(endpoint.Port == firstPort && attempt >= 2)
                    return StratumServer.CreateBoundSocket(endpoint);

                throw new SocketException(
                    (int) SocketError.AddressAlreadyInUse);
            }, addressInUseRetryWindow: TimeSpan.FromMilliseconds(750),
            retryWait: (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });
        var pool = new PoolConfig
        {
            Id = "shared-budget",
            Enabled = true,
            EnableInternalStratum = true,
            Ports = new Dictionary<int, PoolEndpoint>
            {
                [firstPort] = new()
                {
                    Difficulty = 1,
                    ListenAddress = "127.0.0.1",
                },
                [secondPort] = new()
                {
                    Difficulty = 1,
                    ListenAddress = "127.0.0.1",
                },
            },
        };

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { pool }));

        Assert.Equal(new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250),
        }, waits);
        Assert.Equal(TimeSpan.FromMilliseconds(750), waits.Aggregate(
            TimeSpan.Zero, (total, delay) => total + delay));
        Assert.Equal(2, attempts[firstPort]);
        Assert.Equal(3, attempts[secondPort]);
        Assert.Contains(
            "after exhausting the shared 0.75-second startup retry-delay budget",
            error.Message, StringComparison.Ordinal);
        using var reacquired = StratumServer.CreateBoundSocket(
            new IPEndPoint(IPAddress.Loopback, firstPort));
    }

    [Fact]
    public async Task NativeBindingFailure_IsWrappedAsPoolStartupException()
    {
        var coordinator = new StratumListenerReservationCoordinator(_ =>
            throw new DllNotFoundException("injected libc resolution failure"));
        var pool = CreatePool("native-failure", 3032, "127.0.0.1");

        var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
            coordinator.ReserveAllAsync(new[] { pool }));

        Assert.Equal(pool.Id, error.PoolId);
        Assert.IsType<DllNotFoundException>(error.InnerException);
        Assert.Contains("DllNotFoundException", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("injected libc resolution failure", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddressInUse_RetryHonorsStartupCancellation()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        IPEndPoint firstEndpoint = null;
        var coordinator = new StratumListenerReservationCoordinator(endpoint =>
            {
                attempts++;

                if(attempts == 1)
                {
                    var socket = StratumServer.CreateBoundSocket(endpoint);
                    firstEndpoint = (IPEndPoint) socket.LocalEndPoint;
                    return socket;
                }

                throw new SocketException(
                    (int) SocketError.AddressAlreadyInUse);
            }, addressInUseRetryWindow: TimeSpan.FromSeconds(90),
            retryWait: (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var pools = new[]
        {
            CreatePool("already-reserved", 0, "127.0.0.1"),
            CreatePool("cancelled", 3032, "127.0.0.1"),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ReserveAllAsync(pools, cts.Token));
        Assert.Equal(2, attempts);
        Assert.NotNull(firstEndpoint);
        using var reacquired = StratumServer.CreateBoundSocket(firstEndpoint);
    }

    private static StratumListenerReservationCoordinator
        CreateCoordinatorWithoutRetry() => new(
            StratumServer.CreateBoundSocket,
            addressInUseRetryWindow: TimeSpan.Zero);

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
