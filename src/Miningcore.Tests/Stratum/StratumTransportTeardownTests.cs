using System;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Stratum;

public class StratumTransportTeardownTests : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EthereumRequest_DrainingAfterPeerEofOrHostStop_DoesNotReportConnectionFailure(bool hostStop)
    {
        using var scope = container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(Substitute.For<IBlockRepository>());
            builder.RegisterInstance(Substitute.For<IShareRepository>());
        });
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var streams = new RecyclableMemoryStreamManager();
        var pool = new EthereumDispatchPool(scope, clock, streams);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint());
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(endpoint.IPEndPoint);
        using var socket = await listener.AcceptSocketAsync();
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory), streams, clock, "ethereum-drain", false);
        connection.SetContext(new EthereumWorkerContext { ProtocolVersion = 2 });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception error = null;
        var completions = 0;
        var dispatch = connection.DispatchAsync(socket, stop.Token, endpoint, (IPEndPoint) socket.RemoteEndPoint, null,
            async (worker, request, ct) =>
            {
                using var registration = ct.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await pool.Dispatch(worker, request, ct);
            }, _ => completions++, (_, failure) => error = failure);
        try
        {
            await client.GetStream().WriteAsync(StratumConnection.Encoding.GetBytes(
                "{\"id\":1,\"method\":\"mining.extranonce.subscribe\",\"params\":[]}\n"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(hostStop)
                stop.Cancel();
            else
                client.Client.Shutdown(SocketShutdown.Send);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(error);
        Assert.Equal(1, completions);
        Assert.Equal(1, connection.ResponseSequence);
        Assert.Equal(hostStop ? StratumConnectionCompletionReason.HostShutdown : StratumConnectionCompletionReason.PeerEof,
            connection.CompletionReason);
    }

    private sealed class EthereumDispatchPool : EthereumPool
    {
        internal EthereumDispatchPool(IComponentContext ctx, IMasterClock clock, RecyclableMemoryStreamManager streams) :
            base(ctx, new JsonSerializerSettings(), Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(),
                AutoMapperFactory.CreateMapper(), clock, Substitute.For<IMessageBus>(), streams,
                new NicehashService(Substitute.For<System.Net.Http.IHttpClientFactory>(),
                    new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))
        {
            poolConfig = new PoolConfig();
        }

        internal Task Dispatch(StratumConnection worker, JsonRpcRequest request, CancellationToken ct) =>
            OnRequestAsync(worker, new Timestamped<JsonRpcRequest>(request, clock.Now), ct);
    }
}
