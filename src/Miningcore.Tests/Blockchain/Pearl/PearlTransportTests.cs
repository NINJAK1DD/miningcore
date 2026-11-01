using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

[Collection("Pearl proof worker processes")]
public class PearlTransportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargeFrameCrossesPipeThresholdOnlyAfterAuthorization(bool authorized)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint);
        using var socket = await listener.AcceptSocketAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var connection = new StratumConnection(LogManager.CreateNullLogger(), new RecyclableMemoryStreamManager(),
            clock, "pearl-transport", false, default, StratumInputPolicy.Pearl);
        connection.SetContext(new WorkerContextBase { IsAuthorized = authorized });
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = connection.DispatchAsync(socket, stop.Token,
            new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()),
            (IPEndPoint) socket.RemoteEndPoint, null,
            (_, _, _) => { done.TrySetResult(true); return Task.CompletedTask; },
            _ => done.TrySetResult(false), (_, _) => done.TrySetResult(false));
        try
        {
            var frame = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.submit\",\"params\":{\"plain_proof\":\"" +
                new string('a', 200000) + "\"}}\n");
            try { await client.GetStream().WriteAsync(frame, stop.Token); }
            catch(IOException) when(!authorized) { }
            Assert.Equal(authorized, await done.Task.WaitAsync(stop.Token));
        }
        finally { stop.Cancel(); await dispatched; }
    }

    [Fact]
    public async Task PartialProofHasDeadlineEvenAfterAuthorization()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint);
        using var socket = await listener.AcceptSocketAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var connection = new StratumConnection(LogManager.CreateNullLogger(), new RecyclableMemoryStreamManager(),
            clock, "pearl-slow-frame", false, default, StratumInputPolicy.Pearl) {ProofFrameTimeout = TimeSpan.FromMilliseconds(100)};
        connection.SetContext(new WorkerContextBase {IsAuthorized = true});
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = connection.DispatchAsync(socket, stop.Token,
            new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()),
            (IPEndPoint) socket.RemoteEndPoint, null, (_, _, _) => {done.TrySetResult(true); return Task.CompletedTask;},
            _ => done.TrySetResult(false), (_, _) => done.TrySetResult(false));
        try
        {
            await client.GetStream().WriteAsync("{\"id\":1,\"method\":\"mining.submit\",\"params\":"u8.ToArray(), stop.Token);
            Assert.False(await done.Task.WaitAsync(stop.Token));
        }
        finally {stop.Cancel(); await dispatch;}
    }

    [Fact]
    public void JsonPreflightRejectsExpensiveNumbersDepthAndDuplicateProperties()
    {
        foreach(var json in new[] {"{\"x\":1,\"x\":2}", "{\"x\":"+new string('1',1000)+"}",
            new string('[',9)+"0"+new string(']',9)})
            Assert.ThrowsAny<Exception>(() => StratumInputPolicy.Pearl.ValidateJson(
                new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json))));
        StratumInputPolicy.Pearl.ValidateJson(new ReadOnlySequence<byte>("{\"id\":1,\"params\":{}}"u8.ToArray()));
    }

    [Fact]
    public void AggregateConnectionAndByteBudgetsHaveHardBoundsAndReleaseOnce()
    {
        var leases = new List<IDisposable>();
        try
        {
            for(var i=0; i<16; i++) leases.Add(StratumInputPolicy.Pearl.Acquire());
            Assert.Throws<IOException>(() => StratumInputPolicy.Pearl.Acquire());
        }
        finally { foreach(var lease in leases) { lease.Dispose(); lease.Dispose(); } }
        using var next = StratumInputPolicy.Pearl.Acquire();
        Assert.Throws<IOException>(() => new StratumInputPolicy.ByteBudget(StratumInputPolicy.Pearl)
            .Charge(StratumInputPolicy.Pearl.MaximumLength + 32769));
        Assert.Null(StratumInputPolicy.Standard.Acquire());
        Assert.Equal(0x8000, StratumInputPolicy.Standard.MaximumLength);
    }
}
