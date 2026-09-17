using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests.Mining;

public class BoundedQueueAccountingTests
{
    [Fact]
    public async Task ConcurrentProducers_RecordExactCapacityAndEveryOverflow()
    {
        const int capacity = 2;
        const int producerCount = 32;
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        var accounting = new BoundedQueueAccounting<int>(capacity);
        using var start = new ManualResetEventSlim();
        var producers = Enumerable.Range(0, producerCount)
            .Select(value => Task.Run(() =>
            {
                start.Wait();
                return accounting.TryWrite(channel.Writer, value);
            }))
            .ToArray();

        start.Set();
        var admitted = await Task.WhenAll(producers);

        Assert.Equal(capacity, admitted.Count(x => x));
        Assert.Equal(producerCount - capacity, accounting.OverflowCount);
        Assert.Equal(capacity, accounting.Depth);
        Assert.Equal(capacity, accounting.HighWatermark);

        while(accounting.TryRead(channel.Reader, out _))
        {
        }

        Assert.Equal(0, accounting.Depth);
        Assert.Equal(capacity, accounting.HighWatermark);
        var snapshot = accounting.GetSnapshot();
        Assert.Equal(0, snapshot.Depth);
        Assert.Equal(capacity, snapshot.HighWatermark);
        Assert.Equal(capacity, snapshot.Capacity);
        Assert.Equal(producerCount - capacity, snapshot.OverflowCount);
    }

    [Fact]
    public async Task ImmediateConcurrentDrain_CannotHideReachedCapacity()
    {
        const int capacity = 2;
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        var accounting = new BoundedQueueAccounting<int>(capacity);

        Assert.True(accounting.TryWrite(channel.Writer, 1));
        Assert.True(accounting.TryWrite(channel.Writer, 2));

        var drain = Task.Run(() =>
        {
            while(accounting.TryRead(channel.Reader, out _))
            {
            }
        });
        var overflow = Task.Run(() => accounting.TryWrite(channel.Writer, 3));
        await Task.WhenAll(drain, overflow);

        while(accounting.TryRead(channel.Reader, out _))
        {
        }

        Assert.Equal(0, accounting.Depth);
        Assert.Equal(capacity, accounting.HighWatermark);
        Assert.InRange(accounting.OverflowCount, 0, 1);
    }

    [Fact]
    public async Task ConcurrentEmergencyProducers_RecordCapacityAndEveryRejection()
    {
        const int capacity = 1;
        const int producerCount = 16;
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        var accounting = new BoundedQueueAccounting<int>(capacity);
        using var start = new ManualResetEventSlim();
        var producers = Enumerable.Range(0, producerCount)
            .Select(value => Task.Run(() =>
            {
                start.Wait();
                return accounting.TryWrite(channel.Writer, value);
            }))
            .ToArray();

        start.Set();
        var admitted = await Task.WhenAll(producers);

        Assert.Single(admitted, x => x);
        Assert.Equal(capacity, accounting.Depth);
        Assert.Equal(capacity, accounting.HighWatermark);
        Assert.Equal(producerCount - capacity, accounting.OverflowCount);
    }
}
