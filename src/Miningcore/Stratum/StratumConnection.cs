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
    StartupTimeout,
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
    internal Func<IPAddress, bool> AdmitProxyIdentity { get; set; }
    internal StratumProxyPolicy ProxyPolicy { get; init; }
    internal TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(10);
    private Action finishStartup;

    private const int MaxInboundRequestLength = 0x8000;
    public static readonly Encoding Encoding = new UTF8Encoding(false);

    private Stream networkStream;
    private Socket socket;
    private readonly Pipe receivePipe;
    private readonly BufferBlock<object> sendQueue;
    private WorkerContextBase context;
    private long responseSequence;

    // Dispatch awaits requests serially. Comparing this sequence before/after a
    // handler detects a response attempt without retaining miner-controlled IDs.
    // Notifications do not advance it. Count before enqueue: a failed send must
    // never make a subsequent error response look safe.
    internal long ResponseSequence => Interlocked.Read(ref responseSequence);
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

        var terminalCallbackSignalled = false;
        // Exactly one of first-request admission and deadline expiry wins. Disarming
        // a timer alone cannot prevent an already queued callback cancelling a handler.
        var startupState = 0; // 0 pending, 1 complete, 2 expired
        StratumConnectionCompletionReason CancellationCompletion() =>
            failStopToken.IsCancellationRequested ? StratumConnectionCompletionReason.MiningFailStop :
            ct.IsCancellationRequested ? StratumConnectionCompletionReason.HostShutdown :
            Volatile.Read(ref startupState) == 2 ? StratumConnectionCompletionReason.StartupTimeout :
            StratumConnectionCompletionReason.IndependentCancellation;

        try
        {
            // Standalone dispatch callers have no accept loop. Production supplies the
            // already frozen listener policy used for transport admission above.
            var proxyPolicy = ProxyPolicy ?? new StratumProxyPolicy(endpoint.PoolEndpoint.TcpProxyProtocol);
            expectingProxyHeader = proxyPolicy.Enabled;
            // prepare socket
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // create stream
            networkStream = new NetworkStream(socket, true);

            // Link fail-stop directly so TLS setup can stop before pipe tasks exist.
            // Established handlers already received this cancellation via the send-task
            // completion path; this removes a scheduling hop, without ending their drain.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, failStopToken);
            using var startupDeadline = new CancellationTokenSource();
            // Declaration order is intentional: registration disposal waits for an
            // in-flight callback before either CTS it touches can be disposed.
            using var startupRegistration = startupDeadline.Token.Register(() =>
            {
                if(Interlocked.CompareExchange(ref startupState, 2, 0) == 0)
                    cts.Cancel();
            });
            startupDeadline.CancelAfter(StartupTimeout);
            finishStartup = () =>
            {
                if(Interlocked.CompareExchange(ref startupState, 1, 0) != 0)
                    throw new OperationCanceledException(cts.Token);
                startupDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            };

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
                    proxyPolicy, onRequestAsync);
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
                    Volatile.Read(ref startupState) != 2 &&
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
                // Deadline wins a simultaneous startup/parser failure: never impose a
                // junk ban for a server deadline. Preserve the failure category at Debug.
                if(error == null || Volatile.Read(ref startupState) == 2)
                {
                    // A peer-driven clean EOF may close gracefully. Host shutdown and the
                    // independent financial fail-stop gate remain abortive so accepted sockets
                    // cannot delay exclusive listener reacquisition.
                    if(peerEof &&
                        Volatile.Read(ref startupState) != 2 &&
                        !ct.IsCancellationRequested &&
                        !failStopToken.IsCancellationRequested)
                    {
                        StratumSocketCleanup.ConfigureGracefulClose(socket);

                        // Cancellation can arrive between the checks above and the linger
                        // update. Re-arm abortive close if either server-owned token won.
                        if(Volatile.Read(ref startupState) == 2 || ct.IsCancellationRequested ||
                            failStopToken.IsCancellationRequested)
                        {
                            StratumSocketCleanup.ConfigureAbortiveClose(socket);
                        }
                    }

                    CompletionReason = peerEof && !ct.IsCancellationRequested &&
                        !failStopToken.IsCancellationRequested && Volatile.Read(ref startupState) != 2
                        ? StratumConnectionCompletionReason.PeerEof : CancellationCompletion();
                    if(error != null)
                        StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.CancelledFailure,
                            ConnectionId, error, completion: CompletionReason);
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
                // TLS detection/authentication precedes the pipe tasks. Deadline or
                // shutdown cancellation here must not enter junk-ban/error handling,
                // even if the TLS implementation wraps cancellation as an I/O error.
                if(Volatile.Read(ref startupState) == 2 || ct.IsCancellationRequested ||
                    failStopToken.IsCancellationRequested)
                {
                    CompletionReason = CancellationCompletion();
                    StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.CancelledFailure,
                        ConnectionId, ex, completion: CompletionReason);
                    onCompleted(this);
                }
                else
                    onError(this, ex);
            }
            else
            {
                // The terminal event has already been consumed. Log and absorb callback or
                // teardown failures so DispatchAsync completes without issuing a second terminal
                // event; a faulted task here would not restore the consumed lifecycle transition.
                StratumDiagnostics.Write(logger, LogLevel.Error, StratumDiagnostics.Event.TerminalCallback,
                    ConnectionId, ex);
            }
        }

        finally
        {
            finishStartup = null;
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
        Interlocked.Increment(ref responseSequence);
        return SendAsync(response);
    }

    public Task NotifyAsync<T>(string method, T payload)
    {
        return NotifyAsync(new JsonRpcRequest<T>(method, payload, null));
    }

    public Task NotifyAsync<T>(JsonRpcRequest<T> request)
    {
        // Responses must use RespondAsync so ResponseSequence advances.
        return SendAsync(request);
    }
    
    // Beam stratum API: https://github.com/BeamMW/beam/wiki/Beam-mining-protocol-API-(Stratum)
    public Task NotifyAsync(object request)
    {
        // Raw Beam notifications only; response payloads must use RespondAsync.
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
        // RespondAsync owns response-attempt tracking (one Interlocked increment
        // per response across pool families); notifications bypass that counter.
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
            StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.ReceiveWait, ConnectionId);

            var memory = receivePipe.Writer.GetMemory(MaxInboundRequestLength + 1);

            // read from network directly into pipe memory
            var cb = await networkStream.ReadAsync(memory, ct);
            if(cb == 0)
                break; // EOF

            StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.Receive, ConnectionId, bytes: cb);

            LastReceive = clock.Now;

            // hand off to pipe
            receivePipe.Writer.Advance(cb);

            var result = await receivePipe.Writer.FlushAsync(ct);
            if(result.IsCompleted)
                break;
        }
    }

    private async Task ProcessReceivePipeAsync(CancellationToken ct,
        StratumProxyPolicy proxyProtocol,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync)
    {
        while(!ct.IsCancellationRequested)
        {
            StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.BufferWait, ConnectionId);

            var result = await receivePipe.Reader.ReadAsync(ct);

            var buffer = result.Buffer;
            SequencePosition? position;

            if(buffer.Length > MaxInboundRequestLength)
                throw new InvalidDataException($"Incoming data exceeds maximum of {MaxInboundRequestLength}");

            StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.Buffer, ConnectionId, bytes: buffer.Length);

            do
            {
                // Scan buffer for line terminator
                position = buffer.PositionOf((byte) '\n');

                // Bound a PROXY line while it is still wire bytes, even if a sender
                // withholds LF. Optional ordinary JSON retains its normal line limit.
                if(expectingProxyHeader)
                    ValidateProxyWireLength(position == null ? buffer : buffer.Slice(0, position.Value), proxyProtocol);

                if(position != null)
                {
                    var slice = buffer.Slice(0, position.Value);

                    var proxyHeader = expectingProxyHeader && ProcessProxyHeader(slice, proxyProtocol);
                    if(AdmitProxyIdentity != null)
                    {
                        if(!AdmitProxyIdentity(RemoteEndpoint.Address))
                            throw new StratumAdmissionException();
                        AdmitProxyIdentity = null;
                    }
                    if(!proxyHeader)
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

        StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.Send, ConnectionId, bytes: stream.Length);

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

        if(finishStartup != null)
        {
            ct.ThrowIfCancellationRequested();
            finishStartup();
            finishStartup = null;
        }

        await onRequestAsync(this, request, ct);
    }

    /// <summary>
    /// Returns true if the line was consumed
    /// </summary>
    private bool ProcessProxyHeader(ReadOnlySequence<byte> seq, StratumProxyPolicy proxyProtocol)
    {
        expectingProxyHeader = false;

        ValidateProxyWireLength(seq, proxyProtocol);
        var line = seq.AsString(Encoding);
        var peerAddress = RemoteEndpoint.Address;

        if(line.StartsWith("PROXY "))
        {
            if(proxyProtocol.IsTrustedPeer(peerAddress))
            {
                StratumDiagnostics.Write(logger, LogLevel.Debug, StratumDiagnostics.Event.ProxyHeader, ConnectionId, bytes: seq.Length);

                RemoteEndpoint = StratumProxyProtocol.Parse(line, RemoteEndpoint);
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

    private static void ValidateProxyWireLength(ReadOnlySequence<byte> seq, StratumProxyPolicy proxyProtocol)
    {
        // The dispatcher strips LF; the remaining header (including CR) is <=106 bytes.
        if(seq.Length > 106 && (proxyProtocol.Mandatory ||
            seq.Slice(0, 6).ToSpan().SequenceEqual("PROXY "u8)))
            throw new InvalidDataException("PROXY v1 header exceeds 107 wire bytes");
    }
}
