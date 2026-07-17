using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Tests.Util;
using Xunit;

namespace Miningcore.Tests.Mining;

public class BtStreamReceiverTests
{
    [Fact]
    public async Task StartAsync_CompletesImmediatelyWithoutEndpoints()
    {
        var receiver = new BtStreamReceiver(
            new MockMasterClock { CurrentTime = DateTime.UtcNow },
            new MessageBus(),
            new ClusterConfig { Pools = Array.Empty<PoolConfig>() });

        await receiver.StartAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_PropagatesInitialSocketSetupFailure()
    {
        var expected = new InvalidOperationException("socket setup failed");
        var receiver = CreateReceiver(() => throw expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.StartAsync(CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task StartAsync_RejectsCanceledStartupBeforeSocketSetup()
    {
        var socketSetupCalled = false;
        var receiver = CreateReceiver(() => socketSetupCalled = true);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            receiver.StartAsync(canceled.Token));

        Assert.False(socketSetupCalled);
    }

    [Fact]
    public async Task StartAsync_WaitsForSocketConfiguration()
    {
        var socketSetupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSocketSetup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = CreateReceiver(
            () =>
            {
                socketSetupEntered.TrySetResult();
                allowSocketSetup.Task.GetAwaiter().GetResult();
            });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var startTask = receiver.StartAsync(stop.Token);
        await socketSetupEntered.Task.WaitAsync(stop.Token);

        Assert.False(startTask.IsCompleted);

        allowSocketSetup.TrySetResult();
        await startTask;
        await receiver.StopAsync(stop.Token);
    }

    private static BtStreamReceiver CreateReceiver(Action beforeSocketSetup) => new(
            new MockMasterClock { CurrentTime = DateTime.UtcNow },
            new MessageBus(),
            new ClusterConfig
            {
                Pools = new[]
                {
                    new PoolConfig
                    {
                        Extra = new Dictionary<string, object>
                        {
                            ["btStream"] = new
                            {
                                url = "tcp://127.0.0.1:1",
                            },
                        },
                    },
                },
            },
            beforeSocketSetup);
}
