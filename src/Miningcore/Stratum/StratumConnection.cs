using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Stratum;

internal enum StratumConnectionCompletionReason
{
    Unknown,
    PeerEof,
    HostShutdown,
    MiningFailStop,
    IndependentCancellation,
}

public class StratumConnection
{
    public StratumConnection(ILogger logger, RecyclableMemoryStreamManager rmsm,
        IMasterClock clock, string connectionId, bool gpdrCompliantLogging,
        CancellationToken failStopToken = default)
    {
        this.logger = logger;
        this.rmsm = rmsm;

        receivePipe = new Pipe(PipeOptions.Default);

        sendQueue = new BufferBlock<object>(new DataflowBlockOptions
        {
            EnsureOrdered = true,
        });

        this.clock = clock;
        ConnectionId = connectionId;
        IsAlive = true;
        this.gpdrCompliantLogging = gpdrCompliantLogging;
        this.failStopToken = failStopToken;
    }

    private readonly ILogger logger;
    private readonly RecyclableMemoryStreamManager rmsm;
    private readonly IMasterClock clock;
    private readonly CancellationToken failStopToken;

    internal Func<object, CancellationToken, Task> SendMessageOverride { get; set; }

    private const int MaxInboundRequestLength = 0x8000;
    public static readonly Encoding Encoding = new UTF8Encoding(false);

    private Stream networkStream;
    private Socket socket;
    private readonly Pipe receivePipe;
    private readonly BufferBlock<object> sendQueue;
    private WorkerContextBase context;
    private readonly Subject<Unit> terminated = new();
    private bool expectingProxyHeader;
    private bool gpdrCompliantLogging;

    private static readonly JsonSerializer serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private const int SendQueueCapacity = 16;
    private static readonly TimeSpan sendTimeout = TimeSpan.FromMilliseconds(5000);

    #region API-Surface

    public async Task DispatchAsync(Socket socket, CancellationToken ct,
        StratumEndpoint endpoint, IPEndPoint remoteEndpoint, X509Certificate2 cert,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync,
        Action<StratumConnection> onCompleted,
        Action<StratumConnection, Exception> onError)
    {
        LocalEndpoint = endpoint.IPEndPoint;
        RemoteEndpoint = remoteEndpoint;
        this.socket = socket;
        // Keep hard process termination and server-initiated shutdown restart-safe by default.
        // Only a clean peer EOF explicitly disarms linger(0) before stream disposal.
        StratumSocketCleanup.ConfigureAbortiveClose(socket);
        // Host cancellation can race clean-EOF classification. Its synchronous callback and
        // the post-classification recheck below guarantee that cancellation cannot leave a
        // Miningcore-terminated accepted socket configured for graceful close.
        using var hostShutdownRegistration = ct.Register(() =>
            StratumSocketCleanup.ConfigureAbortiveClose(socket));
        // Mining fail-stop closes admission before it asks the host to stop. Register the
        // independent token directly so its synchronous cancellation callback establishes
        // abortive linger before connection tasks can unwind and dispose the owning stream.
        using var failStopRegistration = failStopToken.Register(() =>
            StratumSocketCleanup.ConfigureAbortiveClose(socket));

        expectingProxyHeader = endpoint.PoolEndpoint.TcpProxyProtocol?.Enable == true;

        var terminalCallbackSignalled = false;

        try
        {
            // prepare socket
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // create stream
            networkStream = new NetworkStream(socket, true);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            using(var disposables = new CompositeDisposable(networkStream))
            {
                var abortiveOnExceptionalExit = true;
                using var abortiveCloseGuard = Disposable.Create(() =>
                {
                    if(abortiveOnExceptionalExit)
                        StratumSocketCleanup.ConfigureAbortiveClose(socket);
                });

                var tls = endpoint.PoolEndpoint.Tls;

                // auto-detect SSL
                if(endpoint.PoolEndpoint.TlsAuto)
                    tls = await DetectSslHandshake(socket, cts.Token);

                if(tls)
                {
                    var sslStream = new SslStream(networkStream, false);
                    disposables.Add(sslStream);

                    // TLS handshake
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.None,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, cts.Token);

                    networkStream = sslStream;

                    logger.Info(() => $"[{ConnectionId}] {sslStream.SslProtocol.ToString().ToUpperInvariant()}-{sslStream.NegotiatedCipherSuite.ToString().ToUpperInvariant()} Connection from {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");
                }
                else
                    logger.Info(() => $"[{ConnectionId}] Connection from {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");

                // Async I/O loop(s)
                var receiveTask = FillReceivePipeAsync(cts.Token);
                var processTask = ProcessReceivePipeAsync(cts.Token,
                    endpoint.PoolEndpoint.TcpProxyProtocol, onRequestAsync);
                var sendTask = ProcessSendQueueAsync(cts.Token);
                var tasks = new[]
                {
                    receiveTask,
                    processTask,
                    sendTask
                };

                var completedTask = await Task.WhenAny(tasks);
                // Graceful close is permitted only when the network receive loop positively
                // observed peer EOF while both server-owned cancellation sources remained
                // healthy. An independent OCE from request handling or the send timeout is a
                // server-side failure and must retain the default abortive close.
                var peerEof = ReferenceEquals(completedTask, receiveTask) &&
                    receiveTask.IsCompletedSuccessfully &&
                    !ct.IsCancellationRequested &&
                    !failStopToken.IsCancellationRequested;

                // Stop network I/O, but do not declare the connection complete until an in-flight
                // request handler has reached an admitted-or-rejected outcome. Handlers receive
                // cancellation and are then explicitly drained below.
                cts.Cancel();
                sendQueue.Complete();

                Exception error = null;
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch(Exception ex)
                {
                    error = tasks
                        .Where(task => task.IsFaulted)
                        .SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                        .FirstOrDefault(candidate =>
                            candidate is not OperationCanceledException) ??
                        (ex is OperationCanceledException ? null : ex);
                }

                await receivePipe.Reader.CompleteAsync();
                await receivePipe.Writer.CompleteAsync();

                // Signal completion or error
                if(error == null)
                {
                    // A peer-driven clean EOF may close gracefully. Host shutdown and the
                    // independent financial fail-stop gate remain abortive so accepted sockets
                    // cannot delay exclusive listener reacquisition.
                    if(peerEof &&
                        !ct.IsCancellationRequested &&
                        !failStopToken.IsCancellationRequested)
                    {
                        StratumSocketCleanup.ConfigureGracefulClose(socket);

                        // Cancellation can arrive between the checks above and the linger
                        // update. Re-arm abortive close if either server-owned token won.
                        if(ct.IsCancellationRequested ||
                            failStopToken.IsCancellationRequested)
                        {
                            StratumSocketCleanup.ConfigureAbortiveClose(socket);
                        }
                    }

                    CompletionReason = failStopToken.IsCancellationRequested
                        ? StratumConnectionCompletionReason.MiningFailStop
                        : ct.IsCancellationRequested
                            ? StratumConnectionCompletionReason.HostShutdown
                            : peerEof
                                ? StratumConnectionCompletionReason.PeerEof
                                : StratumConnectionCompletionReason.IndependentCancellation;
                    // Set this before invoking external callback code. If the callback or later
                    // stream teardown throws, the outer catch must not signal this connection a
                    // second time and mask the original failure with duplicate unregistration.
                    terminalCallbackSignalled = true;
                    onCompleted(this);
                    abortiveOnExceptionalExit = false;
                }
                else
                {
                    // NetworkStream owns the accepted socket. Configure abortive linger while
                    // it is still alive so malformed requests, TLS failures and handler errors
                    // cannot leave an exclusive listener endpoint in local TIME_WAIT.
                    StratumSocketCleanup.ConfigureAbortiveClose(socket);
                    terminalCallbackSignalled = true;
                    onError(this, error);
                }
            }
        }

        catch(Exception ex)
        {
            // Errors before stream construction have no other socket owner. Errors after it
            // are already configured abortive by the inner scope; this remains race-safe.
            StratumSocketCleanup.CloseAbortively(socket);

            if(!terminalCallbackSignalled)
            {
                terminalCallbackSignalled = true;
                onError(this, ex);
            }
            else
            {
                // The terminal event has already been consumed. Log and absorb callback or
                // teardown failures so DispatchAsync completes without issuing a second terminal
                // event; a faulted task here would not restore the consumed lifecycle transition.
                logger.Error(ex, () =>
                    $"[{ConnectionId}] Terminal connection callback or subsequent teardown failed; refusing a second callback");
            }
        }

        finally
        {
            this.socket = null;

            // Release external observables
            IsAlive = false;
            terminated.OnNext(Unit.Default);

            logger.Info(() => $"[{ConnectionId}] Connection closed");
        }
    }

    public string ConnectionId { get; }
    public IPEndPoint LocalEndpoint { get; private set; }
    public IPEndPoint RemoteEndpoint { get; private set; }
    public DateTime? LastReceive { get; set; }
    public bool IsAlive { get; set; }
    public IObservable<Unit> Terminated => terminated.AsObservable();
    public WorkerContextBase Context => context;
    internal StratumConnectionCompletionReason CompletionReason { get; private set; }

    public void SetContext<T>(T value) where T : WorkerContextBase
    {
        context = value;
    }

    public T ContextAs<T>() where T : WorkerContextBase
    {
        return (T) context;
    }

    public Task RespondAsync<T>(T payload, object id)
    {
        return RespondAsync(new JsonRpcResponse<T>(payload, id));
    }

    public Task RespondErrorAsync(StratumError code, string message, object id, object result = null)
    {
        return RespondAsync(new JsonRpcResponse(new JsonRpcError((int) code, message, null), id, result));
    }

    public Task RespondAsync<T>(JsonRpcResponse<T> response)
    {
        return SendAsync(response);
    }

    public Task NotifyAsync<T>(string method, T payload)
    {
        return NotifyAsync(new JsonRpcRequest<T>(method, payload, null));
    }

    public Task NotifyAsync<T>(JsonRpcRequest<T> request)
    {
        return SendAsync(request);
    }
    
    // Beam stratum API: https://github.com/BeamMW/beam/wiki/Beam-mining-protocol-API-(Stratum)
    public Task NotifyAsync(object request)
    {
        return SendAsync(request);
    }

    public void Disconnect()
    {
        var activeSocket = socket;

        if(activeSocket != null)
            StratumSocketCleanup.ConfigureAbortiveClose(activeSocket);

        networkStream?.Close();
    }

    #endregion // API-Surface

    private Task SendAsync<T>(T payload)
    {
        Contract.RequiresNonNull(payload);

        if(failStopToken.IsCancellationRequested)
            throw new OperationCanceledException(
                "Stratum response rejected by the mining fail-stop gate",
                failStopToken);

        if(sendQueue.Count >= SendQueueCapacity)
            throw new IOException("Sendqueue stalled");

        return sendQueue.SendAsync(payload);
    }

    private async Task FillReceivePipeAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            logger.Debug(() => $"[{ConnectionId}] [NET] Waiting for data ...");

            var memory = receivePipe.Writer.GetMemory(MaxInboundRequestLength + 1);

            // read from network directly into pipe memory
            var cb = await networkStream.ReadAsync(memory, ct);
            if(cb == 0)
                break; // EOF

            logger.Debug(() => $"[{ConnectionId}] [NET] Received data: {Encoding.GetString(memory.Slice(0, cb).Span)}");

            LastReceive = clock.Now;

            // hand off to pipe
            receivePipe.Writer.Advance(cb);

            var result = await receivePipe.Writer.FlushAsync(ct);
            if(result.IsCompleted)
                break;
        }
    }

    private async Task ProcessReceivePipeAsync(CancellationToken ct,
        TcpProxyProtocolConfig proxyProtocol,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync)
    {
        while(!ct.IsCancellationRequested)
        {
            logger.Debug(() => $"[{ConnectionId}] [PIPE] Waiting for data ...");

            var result = await receivePipe.Reader.ReadAsync(ct);

            var buffer = result.Buffer;
            SequencePosition? position;

            if(buffer.Length > MaxInboundRequestLength)
                throw new InvalidDataException($"Incoming data exceeds maximum of {MaxInboundRequestLength}");

            logger.Debug(() => $"[{ConnectionId}] [PIPE] Received data: {result.Buffer.AsString(Encoding)}");

            do
            {
                // Scan buffer for line terminator
                position = buffer.PositionOf((byte) '\n');

                if(position != null)
                {
                    var slice = buffer.Slice(0, position.Value);

                    if(!expectingProxyHeader || !ProcessProxyHeader(slice, proxyProtocol))
                        await ProcessRequestAsync(ct, onRequestAsync, slice);

                    // Skip consumed section
                    buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                }
            } while(position != null);

            receivePipe.Reader.AdvanceTo(buffer.Start, buffer.End);

            if(result.IsCompleted)
                break;
        }
    }

    private async Task<bool> DetectSslHandshake(Socket socket, CancellationToken ct)
    {
        // https://tls.ulfheim.net/
        // https://tls13.ulfheim.net/

        const int BufSize = 1;
        var buf = ArrayPool<byte>.Shared.Rent(BufSize);

        try
        {
            var cb = await socket.ReceiveAsync(buf.AsMemory()[..BufSize], SocketFlags.Peek, ct);

            if(cb == 0)
                return false;   // End of stream

            if(cb < BufSize)
                throw new Exception($"Failed to peek at connection's first {BufSize} byte(s)");

            switch(buf[0])
            {
                case 0x16: // TLS 1.0 - 1.3
                    return true;
            }
        }

        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return false;
    }

    internal async Task ProcessSendQueueAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct,
            failStopToken);
        var sendCt = linked.Token;

        while(!sendCt.IsCancellationRequested)
        {
            if(sendQueue.Count >= SendQueueCapacity)
                throw new IOException($"Send-queue overflow at {sendQueue.Count} of {SendQueueCapacity} items");

            var msg = await sendQueue.ReceiveAsync(sendCt);
            sendCt.ThrowIfCancellationRequested();

            if(SendMessageOverride != null)
                await SendMessageOverride(msg, sendCt);
            else
                await SendMessage(msg, sendCt);
        }
    }

    private async Task SendMessage(object msg, CancellationToken ct)
    {
        await using var stream = rmsm.GetStream(nameof(StratumConnection)) as RecyclableMemoryStream;

        // serialize
        await using (var writer = new StreamWriter(stream!, Encoding, -1, true))
        {
            serializer.Serialize(writer, msg);
        }

        logger.Debug(() => $"[{ConnectionId}] Sending: {Encoding.GetString(stream.GetReadOnlySequence())}");

        // append newline
        stream.WriteByte((byte) '\n');

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(sendTimeout);

        // send
        stream.Position = 0;
        await stream.CopyToAsync(networkStream, cts.Token);
        await networkStream.FlushAsync(cts.Token);
    }

    private async Task ProcessRequestAsync(
        CancellationToken ct,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync,
        ReadOnlySequence<byte> lineBuffer)
    {
        await using var stream = rmsm.GetStream(nameof(StratumConnection), lineBuffer.ToSpan()) as RecyclableMemoryStream;
        using var reader = new JsonTextReader(new StreamReader(stream!, Encoding));

        var request = serializer.Deserialize<JsonRpcRequest>(reader);

        if(request == null)
            throw new JsonException("Unable to deserialize request");

        await onRequestAsync(this, request, ct);
    }

    /// <summary>
    /// Returns true if the line was consumed
    /// </summary>
    private bool ProcessProxyHeader(ReadOnlySequence<byte> seq, TcpProxyProtocolConfig proxyProtocol)
    {
        expectingProxyHeader = false;

        var line = seq.AsString(Encoding);
        var peerAddress = RemoteEndpoint.Address;

        if(line.StartsWith("PROXY "))
        {
            var proxyAddresses = proxyProtocol.ProxyAddresses?.Select(IPAddress.Parse).ToArray();
            if(proxyAddresses == null || !proxyAddresses.Any())
                proxyAddresses = new[] { IPAddress.Loopback, IPUtils.IPv4LoopBackOnIPv6, IPAddress.IPv6Loopback };

            if(proxyAddresses.Any(x => x.Equals(peerAddress)))
            {
                logger.Debug(() => $"[{ConnectionId}] Received Proxy-Protocol header: {line}");

                // split header parts
                var parts = line.Split(" ");
                var remoteAddress = parts[2];
                var remotePort = parts[4];

                // Update client
                RemoteEndpoint = new IPEndPoint(IPAddress.Parse(remoteAddress), int.Parse(remotePort));
                logger.Info(() => $"Real-IP via Proxy-Protocol: {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}");
            }

            else
            {
                throw new InvalidDataException($"Received spoofed Proxy-Protocol header from {peerAddress}");
            }

            return true;
        }

        if(proxyProtocol.Mandatory)
        {
            throw new InvalidDataException($"Missing mandatory Proxy-Protocol header from {peerAddress}. Closing connection.");
        }

        return false;
    }
}
