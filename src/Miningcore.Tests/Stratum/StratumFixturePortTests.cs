using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumFixturePortTests
{
    private static Socket Bind()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    [Fact]
    public void DuplicateNumericPortRemainsBoundDuringRetryAndUniqueSocketIsRetained()
    {
        using var duplicate = Bind();
        using var unique = Bind();
        var used = new HashSet<int> { ((IPEndPoint) duplicate.LocalEndPoint).Port };
        var calls = 0;
        var result = StratumAdmissionTests.Server.BindDistinctPort(IPAddress.Loopback, used,
            _ =>
            {
                if(++calls == 1) return duplicate;
                Assert.False(duplicate.SafeHandle.IsClosed);
                return unique;
            });
        Assert.Same(unique, result);
        Assert.Equal(2, calls);
        Assert.True(duplicate.SafeHandle.IsClosed);
        Assert.False(unique.SafeHandle.IsClosed);
        Assert.Contains(((IPEndPoint) unique.LocalEndPoint).Port, used);
    }

    [Fact]
    public void RepeatedCollisionsAreBoundedAndEveryRejectedSocketIsDisposed()
    {
        var used = new HashSet<int>();
        var sockets = new List<Socket>();
        try
        {
            Assert.Throws<InvalidOperationException>(() => StratumAdmissionTests.Server.BindDistinctPort(
                IPAddress.Loopback, used, _ =>
                {
                    Assert.All(sockets, socket => Assert.False(socket.SafeHandle.IsClosed));
                    var socket = Bind();
                    used.Add(((IPEndPoint) socket.LocalEndPoint).Port);
                    sockets.Add(socket);
                    return socket;
                }));
            Assert.Equal(32, sockets.Count);
            Assert.All(sockets, socket => Assert.True(socket.SafeHandle.IsClosed));
        }
        finally { foreach(var socket in sockets) socket.Dispose(); }
    }

    [Fact]
    public void BindFailureReleasesPreviouslyRejectedSockets()
    {
        using var duplicate = Bind();
        var used = new HashSet<int> { ((IPEndPoint) duplicate.LocalEndPoint).Port };
        var calls = 0;
        Assert.Throws<SocketException>(() => StratumAdmissionTests.Server.BindDistinctPort(
            IPAddress.Loopback, used, _ =>
            {
                if(++calls == 1) return duplicate;
                Assert.False(duplicate.SafeHandle.IsClosed);
                throw new SocketException((int) SocketError.AddressNotAvailable);
            }));
        Assert.True(duplicate.SafeHandle.IsClosed);
    }
}
