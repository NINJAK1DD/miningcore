using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using NSubstitute;
using Prometheus;
using ProtoBuf;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ShareRelayHardeningTests
{
    [Fact]
    public void ReceiverProvenanceCannotBeSerializedByPublisher()
    {
        var original = new Share { IsRelayed = true, PoolId = "pps", Miner = "miner" };
        using var wire = new MemoryStream();
        Serializer.Serialize(wire, original);
        wire.Position = 0;
        Assert.False(Serializer.Deserialize<Share>(wire).IsRelayed);
    }

    [Fact]
    public async Task OversizedPayloadRejectsAdmissionWithoutQueuingIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        listener.Stop();
        var bus = new MessageBus();
        using var relay = new ShareRelay(new ClusterConfig { ShareRelay = new() { PublishUrl = url } }, bus);
        await relay.StartAsync(CancellationToken.None);
        Assert.ThrowsAny<IOException>(() => bus.SendMessage(new Share { PoolId = "pool",
            Miner = new string('a', ShareRelay.MaximumPayloadBytes + 1) }));
        Assert.Equal(0, relay.GetRelayQueueMetrics().Depth);
        Assert.Equal(0, relay.QueuedBytes);
        Assert.Equal(1, relay.GetRelayQueueMetrics().OverflowCount);
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RelayQueueMetricsRemainAccurateAcrossRepeatedScrapes()
    {
        var provider = Substitute.For<IShareRelayQueueMetricsProvider>();
        provider.GetRelayQueueMetrics().Returns(new SharePersistenceQueueMetricsSnapshot(17, 41, 4096, 2));
        provider.QueuedBytes.Returns(1234L); provider.ByteCapacity.Returns(16L * 1024 * 1024);
        var registry = Metrics.NewCustomRegistry();
        using var publisher = new MetricsPublisher(new MessageBus(), null, Metrics.WithCustomRegistry(registry), registry, provider);
        for(var i = 0; i < 2; i++)
        {
            using var stream = new MemoryStream();
            await registry.CollectAndExportAsTextAsync(stream);
            var metrics = Encoding.UTF8.GetString(stream.ToArray());
            Assert.Contains("miningcore_share_relay_queue_depth 17", metrics);
            Assert.Contains("miningcore_share_relay_queue_high_watermark 41", metrics);
            Assert.Contains("miningcore_share_relay_queue_capacity 4096", metrics);
            Assert.Contains("miningcore_share_relay_queue_bytes 1234", metrics);
            Assert.Contains("miningcore_share_relay_queue_byte_capacity 16777216", metrics);
            Assert.Contains("miningcore_share_relay_queue_rejected_total 2", metrics);
        }
    }
}
