using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
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
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Prometheus;
using ProtoBuf;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Mining;

public class ShareRelayHardeningTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentNearLimitPayloads_KeepBoundedBacklogAndStopAdmission()
    {
        const int producers = 64;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"tcp://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}";
        listener.Stop();
        var bus = new MessageBus();
        using var sending = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var start = new ManualResetEventSlim();
        using var relay = new ShareRelay(new ClusterConfig { ShareRelay = new() { PublishUrl = url } }, bus)
        {
            BeforeSend = ct => { sending.Set(); resume.Wait(ct); },
        };
        await relay.StartAsync(CancellationToken.None);
        try
        {
            bus.SendMessage(new Share { PoolId = "pool" });
            Assert.True(sending.Wait(TimeSpan.FromSeconds(10)));
            // Share an immutable input to measure serializer/queue allocations,
            // rather than counting 64 caller-owned strings as relay memory.
            var payload = new string('a', ShareRelay.MaximumPayloadBytes - 1024);
            var allocated = GC.GetTotalAllocatedBytes(true);
            var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var accepted = 0;
            var rejected = 0;
            var tasks = Enumerable.Range(0, producers).Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();
                try
                {
                    bus.SendMessage(new Share { PoolId = "pool", Miner = payload });
                    Interlocked.Increment(ref accepted);
                }
                catch(IOException) { Interlocked.Increment(ref rejected); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            start.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(producers, accepted + rejected);
            Assert.InRange(accepted, 1, 16);
            Assert.Equal(accepted, relay.GetRelayQueueMetrics().Depth);
            Assert.InRange(relay.QueuedBytes, 1, relay.ByteCapacity);
            Assert.Equal(rejected, relay.GetRelayQueueMetrics().OverflowCount);
            Assert.ThrowsAny<IOException>(() => bus.SendMessage(new Share { PoolId = "pool" }));
            using var process = Process.GetCurrentProcess();
            output.WriteLine($"Concurrent near-limit producers={producers}; accepted={accepted}; rejected={rejected}; " +
                $"queueBytes={relay.QueuedBytes}; allocatedBytes={GC.GetTotalAllocatedBytes(true) - allocated}; " +
                $"processPeakRssBytes={process.PeakWorkingSet64}; GC delta=" +
                string.Join(",", Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - collections[g])));
        }
        finally { start.Set(); resume.Set(); await relay.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("committed-source")]
    public void FinancialSnapshotPreservesIdentityWithoutMutatingEitherProjection(string source)
    {
        var paired = new Share { Source = source, AccountingId = Guid.NewGuid().ToString("N"), BlockReward = 2.5m };
        var original = new Share { Source = source, AccountingId = paired.AccountingId,
            BlockReward = 1.25m, PairedShare = paired };
        var snapshot = original.CreateRelaySnapshot("different-publisher");
        Assert.NotSame(original, snapshot);
        Assert.NotSame(paired, snapshot.PairedShare);
        Assert.Equal(source, snapshot.Source);
        Assert.Equal(source, snapshot.PairedShare.Source);
        Assert.Equal(source, original.Source);
        Assert.Equal(source, paired.Source);
        Assert.Equal(0, original.BlockRewardDouble);
        Assert.Equal(0, paired.BlockRewardDouble);
        Assert.Equal(1.25, snapshot.BlockRewardDouble);
        Assert.Equal(2.5, snapshot.PairedShare.BlockRewardDouble);
        var ordinary = new Share { Source = "original" };
        Assert.Equal("publisher", ordinary.CreateRelaySnapshot("publisher").Source);
        Assert.Equal("original", ordinary.Source);
    }

    [Theory]
    [InlineData(PayoutScheme.PPLNS, false)]
    [InlineData(PayoutScheme.PROP, false)]
    [InlineData(PayoutScheme.PPLNS, true)]
    [InlineData(PayoutScheme.PROP, true)]
    public async Task NonPpsRelayFailureStopsAdmission(PayoutScheme scheme, bool publisherFailure)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"tcp://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}";
        listener.Stop();
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(), Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus(coordinator);
        using var sending = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var relay = new ShareRelay(new ClusterConfig
        {
            ShareRelay = new() { PublishUrl = url },
            Pools = new[] { new PoolConfig { Id = "ordinary", Enabled = true,
                PaymentProcessing = new() { Enabled = true, PayoutScheme = scheme } } },
        }, bus, coordinator)
        {
            QueueCapacity = 2,
            BeforeSend = ct =>
            {
                sending.Set();
                resume.Wait(ct);
                if(publisherFailure) throw new IOException("Injected publisher failure");
            },
        };
        await relay.StartAsync(CancellationToken.None);
        try
        {
            bus.SendMessage(new Share { PoolId = "ordinary" });
            Assert.True(sending.Wait(TimeSpan.FromSeconds(5)));
            if(!publisherFailure)
            {
                bus.SendMessage(new Share { PoolId = "ordinary" });
                bus.SendMessage(new Share { PoolId = "ordinary" });
                Assert.ThrowsAny<IOException>(() => bus.SendMessage(new Share { PoolId = "ordinary" }));
                Assert.Equal(2, relay.GetRelayQueueMetrics().Depth);
                Assert.Equal(1, relay.GetRelayQueueMetrics().OverflowCount);
            }
            resume.Set();
            Assert.True(SpinWait.SpinUntil(() => coordinator.IsFailStopRequested, TimeSpan.FromSeconds(5)));
            Assert.ThrowsAny<Exception>(() => bus.SendMessage(new Share { PoolId = "ordinary" }));
        }
        finally { resume.Set(); await relay.StopAsync(CancellationToken.None); }
    }

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
