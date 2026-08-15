using System;
using System.Net;
using System.Net.Sockets;
using Miningcore.Stratum;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumListenerPortabilityTests
{
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

            Assert.Equal(SocketError.AddressAlreadyInUse,
                error.SocketErrorCode);
        }
        finally
        {
            first.Dispose();
        }

        using var restarted = StratumServer.CreateBoundSocket(endpoint);
        Assert.True(restarted.IsBound);
        Assert.Equal(endpoint, restarted.LocalEndPoint);
    }
}
