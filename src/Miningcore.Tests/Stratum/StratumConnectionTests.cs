using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

#pragma warning disable 8974

namespace Miningcore.Tests.Stratum;

public class StratumConnectionTests : TestBase
{
    private sealed class ReceiptStream : System.IO.Stream
    {
        internal readonly System.Threading.Channels.Channel<byte[]> Chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if(!await Chunks.Reader.WaitToReadAsync(ct)) return 0;
            var bytes = await Chunks.Reader.ReadAsync(ct);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraceUsesReadContainingTerminatorAcrossHandlerBacklog(bool lateTerminator)
    {
        var time = new ManualTimeProvider();
        var worker = new WorkerContextBase();
        worker.Init(10, null, clock, time);
        var old = worker.JobForDifficulty(new object(), (_, _) => new object(), "1");
        worker.SetDifficulty(40);
        var connection = new StratumConnection(logger, rmsm, clock, "receipt-test", false);
        connection.SetContext(worker);
        var wrapper = new PrivateObject(connection);
        var input = new ReceiptStream();
        wrapper.SetField("networkStream", input);
        var pipe = (Pipe) wrapper.GetField("receivePipe");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> handler = async (_, request, _) =>
        {
            if(Convert.ToInt32(request.Id) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TestTimeout);
            }
            else
            {
                var accepted = worker.TryValidateShareDifficulty(20, false, old, out var credit);
                if(accepted) Assert.Equal(10, credit);
                result.TrySetResult(accepted);
            }
        };
        using var stop = new CancellationTokenSource(TestTimeout);
        var fill = (Task) wrapper.Invoke("FillReceivePipeAsync", stop.Token);
        var process = (Task) wrapper.Invoke("ProcessReceivePipeAsync", stop.Token,
            new StratumProxyPolicy(null), handler);
        try
        {
            time.AdvanceMonotonic(TimeSpan.FromSeconds(29));
            await input.Chunks.Writer.WriteAsync(Encoding.UTF8.GetBytes(
                "{\"id\":1,\"method\":\"test\"}\n{\"id\":2,\"method\":\"test\",\"receivedAt\":0}" + (lateTerminator ? "" : "\n")));
            await entered.Task.WaitAsync(TestTimeout);
            time.AdvanceMonotonic(TimeSpan.FromSeconds(31));
            if(lateTerminator) await input.Chunks.Writer.WriteAsync(new byte[] { (byte) '\n' });
            input.Chunks.Writer.Complete();
            await fill.WaitAsync(TestTimeout);
            await pipe.Writer.CompleteAsync();
            release.TrySetResult();
            Assert.Equal(!lateTerminator, await result.Task.WaitAsync(TestTimeout));
            await process.WaitAsync(TestTimeout);
            Assert.False(worker.TryValidateShareDifficulty(20, false, old, out _)); // Ambient receipt restored.
        }
        finally
        {
            release.TrySetResult();
            input.Chunks.Writer.TryComplete();
            stop.Cancel();
            await pipe.Reader.CompleteAsync();
        }
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const string JsonRpcVersion = "2.0";
    private const string ConnectionId = "foo";
    private const string requestString = "{\"params\": [\"slush.miner1\", \"password\"], \"id\": 42, \"method\": \"mining.authorize\"}\\n";
    private const string ProcessRequestAsyncMethod = "ProcessRequestAsync";

    private static readonly RecyclableMemoryStreamManager rmsm = ModuleInitializer.Container.Resolve<RecyclableMemoryStreamManager>();
    private static readonly IMasterClock clock = ModuleInitializer.Container.Resolve<IMasterClock>();
    private static readonly ILogger logger = new NullLogger(LogManager.LogFactory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedRequests_StopAtEachLineAfterDisconnectOrCancellation(bool cancel)
    {
        var connection = new StratumConnection(logger, rmsm, clock, ConnectionId, false);
        var wrapper = new PrivateObject(connection);
        var pipe = (Pipe) wrapper.GetField("receivePipe");
        // Fill a single ReadResult deterministically. TCP tests separately drive
        // the real transport; they cannot guarantee OS packet coalescing.
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(
            "{\"id\":42,\"method\":\"mining.subscribe\"}\n",
            "{\"id\":42,\"method\":\"mining.authorize\"}\n",
            "{\"id\":42,\"method\":\"mining.submit\"}\n")));
        await pipe.Writer.CompleteAsync();
        using var stop = new CancellationTokenSource();
        var dispatched = 0;
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> handler = (worker, _, _) =>
        {
            dispatched++;
            if(cancel)
                stop.Cancel();
            else
                worker.Disconnect();
            return Task.CompletedTask;
        };
        var processing = (Task) wrapper.Invoke("ProcessReceivePipeAsync", stop.Token,
            new StratumProxyPolicy(null), handler);
        if(cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));
        else
            await processing.WaitAsync(TestTimeout);
        Assert.Equal(1, dispatched);
        // A fresh read must be legal even when the loop exits mid-buffer.
        // It must also expose only the two undispatched lines.
        var remaining = await pipe.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        Assert.Equal(2, Encoding.UTF8.GetString(remaining.Buffer.ToArray()).Split('\n',
            StringSplitOptions.RemoveEmptyEntries).Length);
        pipe.Reader.AdvanceTo(remaining.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public void RespondAsync_FailStopTokenRejectsAcknowledgement()
    {
        using var failStop = new CancellationTokenSource();
        failStop.Cancel();
        var connection = new StratumConnection(logger, rmsm, clock,
            ConnectionId, false, failStop.Token);

        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = connection.RespondAsync(true, 1);
        });
    }

    [Fact]
    public async Task ProcessSendQueue_FailStopCancelsQueuedResponseBeforeTransmissionCompletes()
    {
        using var failStop = new CancellationTokenSource();
        var connection = new StratumConnection(logger, rmsm, clock,
            ConnectionId, false, failStop.Token);
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transmitted = false;
        connection.SendMessageOverride = async (_, ct) =>
        {
            sendStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            transmitted = true;
        };

        var sendLoop = connection.ProcessSendQueueAsync(CancellationToken.None);
        await connection.RespondAsync(true, 1);
        await sendStarted.Task.WaitAsync(TestTimeout);

        failStop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sendLoop.WaitAsync(TestTimeout));
        Assert.False(transmitted);
    }

    [Fact]
    public async Task DispatchAsync_CompletionCallbackFailure_CompletesWithoutSignallingErrorAgain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new StratumEndpoint(
            (IPEndPoint) listener.LocalEndpoint, new PoolEndpoint());
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var acceptTask = listener.AcceptSocketAsync();
        await client.ConnectAsync(endpoint.IPEndPoint.Address,
            endpoint.IPEndPoint.Port, CancellationToken.None);
        using var accepted = await acceptTask;
        var remoteEndpoint = (IPEndPoint) accepted.RemoteEndPoint;
        var connection = new StratumConnection(logger, rmsm, clock,
            ConnectionId, false);
        var completionCalls = 0;
        var errorCalls = 0;

        var dispatch = connection.DispatchAsync(accepted,
            CancellationToken.None, endpoint, remoteEndpoint, null,
            (_, _, _) => Task.CompletedTask,
            _ =>
            {
                completionCalls++;
                throw new InvalidOperationException(
                    "injected completion callback failure");
            },
            (_, _) => errorCalls++);

        client.Dispose();
        await dispatch.WaitAsync(TestTimeout);

        Assert.Equal(1, completionCalls);
        Assert.Equal(0, errorCalls);
    }

    [Fact]
    public async Task DispatchAsync_ErrorCallbackFailure_CompletesWithoutSignallingTerminalEventAgain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new StratumEndpoint(
            (IPEndPoint) listener.LocalEndpoint, new PoolEndpoint());
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var acceptTask = listener.AcceptSocketAsync();
        await client.ConnectAsync(endpoint.IPEndPoint.Address,
            endpoint.IPEndPoint.Port, CancellationToken.None);
        using var accepted = await acceptTask;
        var remoteEndpoint = (IPEndPoint) accepted.RemoteEndPoint;
        var connection = new StratumConnection(logger, rmsm, clock,
            ConnectionId, false);
        var completionCalls = 0;
        var errorCalls = 0;

        var dispatch = connection.DispatchAsync(accepted,
            CancellationToken.None, endpoint, remoteEndpoint, null,
            (_, _, _) => Task.CompletedTask,
            _ => completionCalls++,
            (_, _) =>
            {
                errorCalls++;
                throw new InvalidOperationException(
                    "injected error callback failure");
            });
        await using var stream = client.GetStream();
        await stream.WriteAsync(StratumConnection.Encoding.GetBytes(
            "not-json\n"));
        await stream.FlushAsync();

        await dispatch.WaitAsync(TestTimeout);

        Assert.Equal(0, completionCalls);
        Assert.Equal(1, errorCalls);
    }

    [Fact]
    public async Task ProcessRequest_Handle_Valid_Request()
    {
        var connection = new StratumConnection(logger, rmsm, clock, ConnectionId, false);
        var wrapper = new PrivateObject(connection);

        Task handler(StratumConnection con, JsonRpcRequest request, CancellationToken ct)
        {
            Assert.Equal(request.JsonRpc, JsonRpcVersion);
            Assert.Equal((long) request.Id, 42);
            Assert.Equal(request.Method, "mining.authorize");
            Assert.True(request.Params is JArray);
            Assert.Equal(request.ParamsAs<JArray>().Count, 2);
            Assert.Equal(request.ParamsAs<JArray>()[0], "slush.miner1");
            Assert.Equal(request.ParamsAs<JArray>()[1], "password");

            return Task.CompletedTask;
        }

        await (Task) wrapper.Invoke(ProcessRequestAsyncMethod,
            CancellationToken.None,
            handler,
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(requestString)));
    }

    [Fact]
    public async Task ProcessRequest_Throw_On_Unparseable_Request()
    {
        const string invalidRequestString = "foo bar\\n";

        var connection = new StratumConnection(logger, rmsm, clock, ConnectionId, false);
        var wrapper = new PrivateObject(connection);
        var callCount = 0;

        Task handler(StratumConnection con, JsonRpcRequest request, CancellationToken ct)
        {
            callCount++;
            return Task.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<JsonException>(()=> (Task) wrapper.Invoke(ProcessRequestAsyncMethod,
            CancellationToken.None,
            handler,
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(invalidRequestString))));

        Assert.Equal(callCount, 0);
    }

    [Fact]
    public async Task ProcessRequest_Honor_CancellationToken()
    {
        var connection = new StratumConnection(logger, rmsm, clock, ConnectionId, false);
        var wrapper = new PrivateObject(connection);
        var callCount = 0;
        using var cts = new CancellationTokenSource();

        Task handler(StratumConnection con, JsonRpcRequest request, CancellationToken ct)
        {
            callCount++;
            Assert.Equal(cts.Token, ct);
            Assert.False(ct.IsCancellationRequested);
            // Cancel only after the handler owns the request. No competing
            // timers or runner continuations decide whether cancellation wins.
            cts.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        var processing = (Task) wrapper.Invoke(ProcessRequestAsyncMethod,
            cts.Token,
            handler,
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(requestString)));
        var error = await Assert.ThrowsAnyAsync<TaskCanceledException>(() => processing);

        Assert.True(processing.IsCanceled);
        Assert.Equal(1, callCount);
        Assert.Equal(cts.Token, error.CancellationToken);
    }

    // [Fact]
    // public async Task DetectSslHandshake_Positive()
    // {
    //     const string MethodName = "DetectSslHandshake";
    //
    //     var connection = new StratumConnection(logger, rmsm, clock, ConnectionId);
    //     var wrapper = new PrivateObject(connection);
    //
    //     var socket = Substitute.For<Socket>(SocketType.Stream, ProtocolType.Tcp);
    //     var buf = new byte[1];
    //
    //     socket.ReceiveAsync(buf, SocketFlags.Peek, CancellationToken.None).ReturnsForAnyArgs(ValueTask.FromResult(1)).AndDoes(info =>
    //     {
    //         var _buf = info.ArgAt<Memory<byte>>(0);
    //         _buf.Span[0] = 0x16;
    //     });
    //
    //     var result = await (Task<bool>) wrapper.Invoke(MethodName,
    //         socket,
    //         CancellationToken.None);
    //
    //     Assert.True(result);
    // }
}
