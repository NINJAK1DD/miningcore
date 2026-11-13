using System.Reactive.Linq;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Rpc;
using NLog;
using ProtoBuf;
using ZeroMQ;

namespace Miningcore.Mining;

public interface IShareRelayQueueMetricsProvider
{
    SharePersistenceQueueMetricsSnapshot GetRelayQueueMetrics();
    long QueuedBytes { get; }
    long ByteCapacity { get; }
}

public sealed class ShareRelay : IHostedService, IDisposable, IShareRelayQueueMetricsProvider
{
    public ShareRelay(ClusterConfig clusterConfig, IMessageBus messageBus,
        IMiningFailStopCoordinator failStopCoordinator = null)
    {
        this.clusterConfig = clusterConfig ?? throw new ArgumentNullException(nameof(clusterConfig));
        this.messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        this.failStopCoordinator = failStopCoordinator;
    }

    private readonly IMessageBus messageBus;
    private readonly ClusterConfig clusterConfig;
    private readonly IMiningFailStopCoordinator failStopCoordinator;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private Channel<Frame> queue;
    private Task publisher;
    private IDisposable subscription;
    private int depth, highWatermark, failed, disposed;
    private long queuedBytes, overflows;
    internal int QueueCapacity { get; init; } = 4096;
    public long ByteCapacity => 16 * 1024 * 1024;
    internal const int MaximumPayloadBytes = 1024 * 1024;
    // Deterministic transport throttling for overload tests, not a user setting.
    internal Action<CancellationToken> BeforeSend { get; init; }
    public long QueuedBytes { get { lock(gate) return queuedBytes; } }
    public SharePersistenceQueueMetricsSnapshot GetRelayQueueMetrics()
    { lock(gate) return new(depth, highWatermark, QueueCapacity, overflows); }
    private sealed record Frame(string Topic, int Flags, byte[] Payload);
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    [Flags]
    public enum WireFormat { Json = 1, ProtocolBuffers = 2, ProtocolBuffersAccounting = 3 }
    public const int WireFormatMask = 0xF;

    public async Task StartAsync(CancellationToken ct)
    {
        ShareRelaySecurity.Validate(clusterConfig);
        queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher = Task.Factory.StartNew(() => Publish(ready), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await ready.Task.WaitAsync(ct);
            subscription = messageBus.Listen<Share>().Subscribe(Enqueue);
        }
        catch { shutdown.Cancel(); throw; }
    }

    private void Enqueue(Share share)
    {
        try
        {
            if(Volatile.Read(ref failed) != 0)
                throw new IOException("Share relay admission is closed");
            share.Source = clusterConfig.ClusterName;
            share.BlockRewardDouble = (double) share.BlockReward;
            if(share.PairedShare != null)
            {
                share.PairedShare.Source = clusterConfig.ClusterName;
                share.PairedShare.BlockRewardDouble = (double) share.PairedShare.BlockReward;
            }
            using var stream = new LimitedPayloadStream();
            Serializer.Serialize(stream, share);
            var frame = new Frame(share.PoolId, (int) (string.IsNullOrEmpty(share.AccountingId)
                ? WireFormat.ProtocolBuffers : WireFormat.ProtocolBuffersAccounting), stream.ToArray());
            lock(gate)
            {
                if(queuedBytes + frame.Payload.Length <= ByteCapacity && queue.Writer.TryWrite(frame))
                {
                    queuedBytes += frame.Payload.Length;
                    highWatermark = Math.Max(highWatermark, ++depth);
                    return;
                }
            }
            throw new IOException("Bounded share relay capacity exhausted");
        }
        catch(Exception error)
        {
            lock(gate) overflows++;
            Interlocked.Exchange(ref failed, 1);
            // Defer the exclusive fail-stop until the publication read gate is released.
            throw new RelayAdmissionFailure(() => StopForFailure(error), error);
        }
    }

    private void Publish(TaskCompletionSource ready)
    {
        try
        {
            // The socket and its disposal belong to this one dedicated thread.
            using var socket = new ZSocket(ZSocketType.PUB)
            { SendHighWatermark = 128, SendTimeout = TimeSpan.FromMilliseconds(500), Linger = TimeSpan.Zero };
            if(clusterConfig.ShareRelay.Connect)
            {
                if(!string.IsNullOrWhiteSpace(clusterConfig.ShareRelay.SharedEncryptionKey))
                    throw new PoolStartupException("ZeroMQ Curve is not supported in ShareRelay Connect-Mode");
                socket.Connect(clusterConfig.ShareRelay.PublishUrl);
            }
            else
            {
                socket.SetupCurveTlsServer(clusterConfig.ShareRelay.SharedEncryptionKey, logger);
                socket.Bind(clusterConfig.ShareRelay.PublishUrl);
            }
            ready.TrySetResult();
            while(queue.Reader.WaitToReadAsync(shutdown.Token).AsTask().GetAwaiter().GetResult())
            {
                Frame frame;
                lock(gate)
                {
                    if(!queue.Reader.TryRead(out frame)) continue;
                    queuedBytes -= frame.Payload.Length;
                    depth--;
                }
                BeforeSend?.Invoke(shutdown.Token);
                using var message = new ZMessage();
                message.Add(new ZFrame(frame.Topic));
                message.Add(new ZFrame(frame.Flags));
                message.Add(new ZFrame(frame.Payload));
                socket.SendMessage(message);
            }
        }
        catch(OperationCanceledException) when(shutdown.IsCancellationRequested) { }
        catch(Exception error)
        {
            ready.TrySetException(error);
            StopForFailure(error);
        }
        finally
        {
            ready.TrySetCanceled();
            queue.Writer.TryComplete();
        }
    }

    private void StopForFailure(Exception error)
    {
        Interlocked.Exchange(ref failed, 1);
        failStopCoordinator?.BeginFailStop(ProcessExitCodes.GeneralFailure);
        queue.Writer.TryComplete();
        RpcConsumerDiagnostics.Write(logger, LogLevel.Fatal, "ShareRelay.Publication", failure: error);
        logger.Fatal("Relay admission stopped. PPS evidence remains owned by the local recorder; preserve accounting and recovery evidence before restart.");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref subscription, null)?.Dispose();
        queue?.Writer.TryComplete();
        if(publisher == null) return;
        try { await publisher.WaitAsync(ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { shutdown.Cancel(); throw; }
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref subscription, null)?.Dispose();
        shutdown.Cancel();
        publisher?.GetAwaiter().GetResult();
        shutdown.Dispose();
    }

    private sealed class RelayAdmissionFailure(Action stop, Exception cause) :
        IOException("Share relay publication was rejected; no positive response is permitted", cause), IMiningAdmissionFailure
    {
        public Task HandleAfterAdmissionReleasedAsync() { stop(); return Task.CompletedTask; }
    }

    private sealed class LimitedPayloadStream : MemoryStream
    {
        private void Check(int count)
        {
            if(count < 0 || Position + count > MaximumPayloadBytes)
                throw new InvalidDataException("Share relay payload exceeds its bounded frame limit");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
