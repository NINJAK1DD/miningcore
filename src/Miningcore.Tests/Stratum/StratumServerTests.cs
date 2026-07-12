using System;
using System.Net;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.JsonRpc;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumServerTests
{
    [Fact]
    public async Task RunAsync_WhenCancelled_StopsIdleListenerPromptly()
    {
        var server = new TestStratumServer();
        using var cts = new CancellationTokenSource();
        var runTask = server.RunListenerAsync(cts.Token);

        cts.Cancel();

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class TestStratumServer : StratumServer
    {
        public TestStratumServer() : base(
            Substitute.For<IComponentContext>(),
            Substitute.For<IMessageBus>(),
            new RecyclableMemoryStreamManager(),
            Substitute.For<IMasterClock>())
        {
            logger = LogManager.GetCurrentClassLogger();
        }

        public Task RunListenerAsync(CancellationToken ct)
        {
            return RunAsync(ct, new StratumEndpoint(
                new IPEndPoint(IPAddress.Loopback, 0), new PoolEndpoint()));
        }

        protected override Task OnRequestAsync(StratumConnection connection,
            Timestamped<JsonRpcRequest> request, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        protected override void OnConnect(StratumConnection connection, IPEndPoint endpoint)
        {
        }
    }
}
