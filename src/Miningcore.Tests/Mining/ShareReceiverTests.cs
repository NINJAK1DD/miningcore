using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Tests.Util;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ShareReceiverTests
{
    [Fact]
    public async Task RelayReceiver_ReconnectsAfterPublisherRestart()
    {
        const string poolId = "relay-pool";
        var port = GetFreePort();
        var url = $"tcp://127.0.0.1:{port}";
        var receiverBus = new MessageBus();
        var clock = new MockMasterClock { CurrentTime = DateTime.UtcNow };
        var receiver = new ShareReceiver(new ClusterConfig
        {
            ShareRelays = new[] { new ShareRelayEndpointConfig { Url = url } },
        }, clock, receiverBus, TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(150));
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(new PoolConfig { Id = poolId });
        pool.NetworkStats.Returns(new BlockchainStats());
        pool.ShareMultiplier.Returns(1d);
        using var receiverStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await receiver.StartAsync(CancellationToken.None);
        receiverBus.SendMessage(new PoolStatusNotification
        {
            Pool = pool,
            Status = PoolStatus.Online,
        });
        await Task.Delay(150);

        ShareRelay relay = null;
        try
        {
            var started = await StartRelayAsync(url);
            relay = started.Relay;
            var senderBus = started.Bus;
            await AssertRelayedAsync(senderBus, receiverBus, poolId, "before-restart");

            await relay.StopAsync(CancellationToken.None);
            relay = null;
            clock.CurrentTime += TimeSpan.FromSeconds(1);
            await Task.Delay(250);

            started = await StartRelayAsync(url);
            relay = started.Relay;
            senderBus = started.Bus;
            await AssertRelayedAsync(senderBus, receiverBus, poolId, "after-restart");
        }

        finally
        {
            if(relay != null)
                await relay.StopAsync(CancellationToken.None);

            await receiver.StopAsync(receiverStop.Token);
        }
    }

    [Fact]
    public void BlockOnlyRelayMessage_IsNotEligibleForOrdinaryShareTelemetry()
    {
        var share = new Share { BlockOnly = true, IsBlockCandidate = true };

        Assert.False(ShareReceiver.IsShareTelemetryEligible(share));
    }

    [Fact]
    public void OrdinaryRelayShare_RemainsEligibleForShareTelemetry()
    {
        Assert.True(ShareReceiver.IsShareTelemetryEligible(new Share()));
    }

    [Fact]
    public void BlockOnlyRelayMessage_PreservesOriginatingCreatedTimestamp()
    {
        var created = DateTime.UtcNow.AddMinutes(-5);
        var share = new Share { BlockOnly = true, Created = created };

        ShareReceiver.NormalizeCreatedTimestamp(share, DateTime.UtcNow);

        Assert.Equal(created, share.Created);
    }

    [Fact]
    public void OrdinaryRelayShare_UsesReceiverCreatedTimestamp()
    {
        var received = DateTime.UtcNow;
        var share = new Share { Created = received.AddMinutes(-5) };

        ShareReceiver.NormalizeCreatedTimestamp(share, received);

        Assert.Equal(received, share.Created);
    }

    [Fact]
    public void EmittedMergedParentShare_PreservesOriginatingCreatedTimestamp()
    {
        var created = DateTime.UtcNow.AddSeconds(-10);
        var share = new Share
        {
            Created = created,
            BlockRecordEmitted = true,
            IsBlockCandidate = false,
        };

        ShareReceiver.NormalizeCreatedTimestamp(share, DateTime.UtcNow);

        Assert.Equal(created, share.Created);
    }

    [Fact]
    public void EarlyMergedParentStatisticalShare_PreservesOriginatingTimestamp()
    {
        var created = DateTime.UtcNow.AddSeconds(-10);
        var share = new Share
        {
            Created = created,
            PreserveCreated = true,
            IsBlockCandidate = false,
        };

        ShareReceiver.NormalizeCreatedTimestamp(share, DateTime.UtcNow);

        Assert.Equal(created, share.Created);
    }

    private static async Task<(ShareRelay Relay, MessageBus Bus)> StartRelayAsync(
        string url)
    {
        var senderBus = new MessageBus();
        var relay = new ShareRelay(new ClusterConfig
        {
            ClusterName = "regtest-sender",
            ShareRelay = new ShareRelayConfig { PublishUrl = url },
        }, senderBus);
        await relay.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        return (relay, senderBus);
    }

    private static async Task AssertRelayedAsync(MessageBus senderBus,
        MessageBus receiverBus, string poolId, string miner)
    {
        var received = new TaskCompletionSource<Share>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = receiverBus.Listen<Share>().Subscribe(x =>
        {
            if(x.Miner == miner)
                received.TrySetResult(x);
        });

        for(var i = 0; i < 30 && !received.Task.IsCompleted; i++)
        {
            senderBus.SendMessage(new Share
            {
                PoolId = poolId,
                Miner = miner,
                BlockOnly = true,
                IsBlockCandidate = true,
                Created = DateTime.UnixEpoch,
            });
            await Task.Delay(100);
        }

        var share = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(poolId, share.PoolId);
        Assert.Equal("regtest-sender", share.Source);
        Assert.True(share.BlockOnly);
        Assert.Equal(DateTime.UnixEpoch, share.Created);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
