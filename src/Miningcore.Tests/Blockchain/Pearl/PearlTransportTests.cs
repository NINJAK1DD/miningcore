using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    [Fact]
    public async Task PipelinedSmallUnauthenticatedFramesCannotReserveProofMemory()
    {
        var policy = StratumInputPolicy.CreatePearl(16);
        using var buffer = policy.CreateBuffer();
        await buffer.AddAsync(32768, false, IPAddress.Loopback, CancellationToken.None, authorized: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => buffer.AddAsync(1, false, IPAddress.Loopback, CancellationToken.None, authorized: false));
        buffer.Consume(32768);
        await buffer.AddAsync(1000000, true, IPAddress.Loopback, CancellationToken.None, authorized: true);
    }

    [Fact]
    public async Task UnauthorizedConnectionChurnReleasesCapacityWithoutReservingProofFrames()
    {
        var policy = StratumInputPolicy.CreatePearl(16, 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task Connect(bool authorized)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint, stop.Token);
            using var socket = await listener.AcceptSocketAsync(stop.Token);
            using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var clock = Substitute.For<IMasterClock>(); clock.Now.Returns(DateTime.UtcNow);
            var connection = new StratumConnection(LogManager.CreateNullLogger(), new RecyclableMemoryStreamManager(),
                clock, "pearl-churn", false, default, policy);
            connection.SetContext(new WorkerContextBase {IsAuthorized = authorized});
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatch = connection.DispatchAsync(socket, connectionStop.Token,
                new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()),
                (IPEndPoint) socket.RemoteEndPoint, null,
                (_, _, _) => { result.TrySetResult(true); return Task.CompletedTask; },
                _ => result.TrySetResult(false), (_, _) => result.TrySetResult(false));
            try
            {
                var bytes = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.submit\",\"params\":{\"plain_proof\":\"" + new string('a', 200000) + "\"}}\n");
                try { await client.GetStream().WriteAsync(bytes, stop.Token); }
                catch(IOException) when(!authorized) { }
                Assert.Equal(authorized, await result.Task.WaitAsync(stop.Token));
            }
            finally { connectionStop.Cancel(); await dispatch; }
        }
        // Occupying the only large-frame reservation must not impede rejecting
        // unauthorized clients, even across repeated waves of real sockets.
        using(var frame = policy.CreateBuffer())
        {
            await frame.AddAsync(32769, true, IPAddress.Loopback, stop.Token);
            for(var wave = 0; wave < 3; wave++)
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Connect(false)));
        }
        await Connect(true);
        var leases = new List<IDisposable>();
        try { for(var i = 0; i < 16; i++) leases.Add(policy.Acquire()); }
        finally { foreach(var lease in leases) lease.Dispose(); }
        using var recoveredFrame = policy.CreateBuffer();
        await recoveredFrame.AddAsync(32769, true, IPAddress.Loopback, stop.Token);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialProofHasDeadlineEvenAfterAuthorization(bool waitingForCapacity)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint);
        using var socket = await listener.AcceptSocketAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var policy = StratumInputPolicy.CreatePearl(16, 1);
        using var blocker = policy.CreateBuffer();
        if(waitingForCapacity) await blocker.AddAsync(65536, true, IPAddress.Loopback, stop.Token);
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var connection = new StratumConnection(LogManager.CreateNullLogger(), new RecyclableMemoryStreamManager(),
            clock, "pearl-slow-frame", false, default, policy) {ProofFrameTimeout = TimeSpan.FromMilliseconds(100)};
        connection.SetContext(new WorkerContextBase {IsAuthorized = true});
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = connection.DispatchAsync(socket, stop.Token,
            new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()),
            (IPEndPoint) socket.RemoteEndPoint, null, (_, _, _) => {done.TrySetResult(true); return Task.CompletedTask;},
            _ => done.TrySetResult(false), (_, _) => done.TrySetResult(false));
        try
        {
            var partial = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.submit\",\"params\":\"" + new string('a', waitingForCapacity ? 65536 : 0));
            await client.GetStream().WriteAsync(partial, stop.Token);
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
        var policy = StratumInputPolicy.CreatePearl(16, 2);
        var otherPool = StratumInputPolicy.CreatePearl(16, 2);
        var leases = new List<IDisposable>();
        try
        {
            for(var i=0; i<16; i++) leases.Add(policy.Acquire());
            Assert.Throws<IOException>(() => policy.Acquire());
            using var isolated = otherPool.Acquire();
        }
        finally { foreach(var lease in leases) { lease.Dispose(); lease.Dispose(); } }
        using var next = policy.Acquire();
        Assert.Throws<IOException>(() => new StratumInputPolicy.ByteBudget(StratumInputPolicy.Pearl)
            .Charge(StratumInputPolicy.Pearl.MaximumLength + 32769));
        Assert.Null(StratumInputPolicy.Standard.Acquire());
        Assert.Equal(0x8000, StratumInputPolicy.Standard.MaximumLength);
    }

    [Fact]
    public async Task UploadBackpressureSeparatesPeerSlotsFromRetainedBytesAndHonorsCancellation()
    {
        var policy = StratumInputPolicy.CreatePearl(16, 16, 64, 4);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffers = Enumerable.Range(0, 4).Select(_ => policy.CreateBuffer()).ToArray();
        try
        {
            foreach(var buffer in buffers) await buffer.AddAsync(65536, true, IPAddress.Loopback, stop.Token);
            using var waiting = policy.CreateBuffer();
            var pending = waiting.AddAsync(65536, true, IPAddress.Parse("::ffff:127.0.0.1"), stop.Token);
            Assert.False(pending.IsCompleted);
            using var other = policy.CreateBuffer();
            await other.AddAsync(65536, true, IPAddress.Parse("127.0.0.2"), stop.Token);
            buffers[0].EndUpload(); // Still retains bytes while verification/commit runs.
            await pending.WaitAsync(stop.Token);
            using var canceled = new CancellationTokenSource();
            using var blocked = policy.CreateBuffer();
            var cancellation = blocked.AddAsync(65536, true, IPAddress.Loopback, canceled.Token);
            Assert.False(cancellation.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellation);
        }
        finally { foreach(var buffer in buffers) { buffer.Dispose(); buffer.Dispose(); } }
        using var recovered = policy.CreateBuffer();
        await recovered.AddAsync(65536, true, IPAddress.Loopback, stop.Token);
    }

    [Fact]
    public async Task ByteBudgetRemainsReservedAfterUploadAndWaitersResumeAfterConsumption()
    {
        var policy = StratumInputPolicy.CreatePearl(16, 16, 64);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffers = Enumerable.Range(0, 8).Select(_ => policy.CreateBuffer()).ToArray();
        try
        {
            for(var i = 0; i < buffers.Length; i++)
            {
                await buffers[i].AddAsync(8 * 1024 * 1024 + 32768, true, IPAddress.Parse($"127.0.0.{i / 2 + 1}"), stop.Token);
                buffers[i].EndUpload();
            }
            using var next = policy.CreateBuffer();
            var pending = next.AddAsync(65536, true, IPAddress.Parse("127.0.0.9"), stop.Token);
            Assert.False(pending.IsCompleted);
            buffers[0].Consume(65536);
            await pending.WaitAsync(stop.Token);
        }
        finally { foreach(var buffer in buffers) buffer.Dispose(); }
        using var recovered = policy.CreateBuffer();
        await recovered.AddAsync(8 * 1024 * 1024, true, IPAddress.Loopback, stop.Token);
    }

    [Fact]
    public async Task SixteenRawProofUploadsCompleteWhileTheirHandlersAreStillBusy()
    {
        var policy = StratumInputPolicy.CreatePearl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Run()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint) listener.LocalEndpoint, stop.Token);
            using var socket = await listener.AcceptSocketAsync(stop.Token);
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var clock = Substitute.For<IMasterClock>(); clock.Now.Returns(DateTime.UtcNow);
            var connection = new StratumConnection(LogManager.CreateNullLogger(), new RecyclableMemoryStreamManager(),
                clock, "pearl-load", false, default, policy);
            connection.SetContext(new WorkerContextBase { IsAuthorized = true });
            var dispatch = connection.DispatchAsync(socket, ended.Token,
                new StratumEndpoint((IPEndPoint) listener.LocalEndpoint, new PoolEndpoint()), (IPEndPoint) socket.RemoteEndPoint, null,
                async (_, _, ct) => { if(Interlocked.Increment(ref arrived) == 16) all.TrySetResult(); await processing.Task.WaitAsync(ct); },
                _ => { }, (_, _) => { });
            try
            {
                var frame = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"mining.submit\",\"params\":{\"plain_proof\":\"" + new string('a', 1070000) + "\"}}\n");
                await client.GetStream().WriteAsync(frame, stop.Token);
                await all.Task.WaitAsync(stop.Token);
            }
            finally { ended.Cancel(); await dispatch; }
        }
        try { await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Run())); }
        finally { processing.TrySetResult(); }
        Assert.Equal(16, arrived);
    }

    [Fact]
    public void PartialFrameScannerExaminesOnlyNewBytes()
    {
        var data = new byte[1024 * 1024];
        Array.Fill(data, (byte) 'a'); data[^1] = (byte) '\n';
        long examined = 0;
        for(var length = 16384; length < data.Length; length += 16384)
        {
            Assert.Null(StratumInputPolicy.FindLine(new ReadOnlySequence<byte>(data.AsMemory(0, length)), ref examined));
            Assert.Equal(length, examined);
        }
        Assert.NotNull(StratumInputPolicy.FindLine(new ReadOnlySequence<byte>(data), ref examined));
        Assert.Equal(0, examined);
    }
}
