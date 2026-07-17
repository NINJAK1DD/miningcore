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
    public async Task StartAsync_WaitsForSocketConfiguration()
    {
        var socketSetupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSocketSetup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new BtStreamReceiver(
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
}
