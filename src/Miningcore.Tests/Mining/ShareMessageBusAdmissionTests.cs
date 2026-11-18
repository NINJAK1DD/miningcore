using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain;
using Miningcore.Messaging;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ShareMessageBusAdmissionTests
{
    [Fact]
    public async Task ConcurrentPublicationCannotReturnBeforeItsSubscriberCompletes()
    {
        var bus = new MessageBus();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var completed = 0;
        using var subscription = bus.Listen<Share>().Subscribe(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
            Interlocked.Increment(ref completed);
        });
        var first = Task.Factory.StartNew(() => bus.SendMessage(new Share()),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Factory.StartNew(() =>
            {
                secondStarted.Set();
                bus.SendMessage(new Share());
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(200)));
            Assert.Equal(0, Volatile.Read(ref completed));
        }
        finally
        {
            release.Set();
            await first;
            if(second != null) await second;
        }
        Assert.Equal(2, completed);
    }

    [Fact]
    public void DetachedFailedSubscriberCannotTurnLaterPublicationIntoSuccess()
    {
        var bus = new MessageBus();
        var calls = 0;
        using var subscription = bus.Listen<Share>().Subscribe(_ =>
        {
            calls++;
            throw new IOException("Injected admission failure");
        });
        Assert.Throws<IOException>(() => bus.SendMessage(new Share()));
        Assert.Throws<InvalidOperationException>(() => bus.SendMessage(new Share()));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => bus.RegisterScheduler<Share>(TaskPoolScheduler.Default));
    }
}
