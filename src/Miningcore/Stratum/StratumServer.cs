using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Autofac;
using Microsoft.IO;
using Microsoft.Win32.SafeHandles;
using Miningcore.Blockchain;
using Miningcore.Banning;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using NLog;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Stratum;

public abstract class StratumServer
{
    protected StratumServer(
        IComponentContext ctx,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        IMasterClock clock)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(rmsm);
        Contract.RequiresNonNull(clock);

        this.ctx = ctx;
        this.messageBus = messageBus;
        this.rmsm = rmsm;
        this.clock = clock;
    }

    static StratumServer()
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ignoredSocketErrors = new HashSet<int>
            {
                (int) SocketError.ConnectionReset,
                (int) SocketError.ConnectionAborted,
                (int) SocketError.OperationAborted
            };
        }

        else if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // see: http://www.virtsync.com/c-error-codes-include-errno
            ignoredSocketErrors = new HashSet<int>
            {
                104, // ECONNRESET
                125, // ECANCELED
                103, // ECONNABORTED
                110, // ETIMEDOUT
                32,  // EPIPE
            };
        }
    }

    protected readonly ConcurrentDictionary<string, StratumConnection> connections = new();
    private readonly ConcurrentDictionary<string, Task> connectionTasks = new();
    // Lets lifecycle tests distinguish connection unregistration from completion of the
    // socket-owning dispatch task without widening the production subclass surface.
    internal int TrackedConnectionTaskCount => connectionTasks.Count;
    protected static readonly ConcurrentDictionary<string, X509Certificate2> certs = new();
    protected static readonly HashSet<int> ignoredSocketErrors;

    protected static readonly MethodBase streamWriterCtor = typeof(StreamWriter).GetConstructor(
        new[] { typeof(Stream), typeof(Encoding), typeof(int), typeof(bool) });

    protected readonly IComponentContext ctx;
    protected readonly IMessageBus messageBus;
    private readonly RecyclableMemoryStreamManager rmsm;
    protected readonly IMasterClock clock;
    protected ClusterConfig clusterConfig;
    protected PoolConfig poolConfig;
    protected IBanManager banManager;
    protected ILogger logger;
    internal TimeSpan ConnectionDrainTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    internal async Task RunAsync(CancellationToken ct,
        params StratumListenerReservation[] listeners)
    {
        Contract.RequiresNonNull(listeners);

        try
        {
            if(listeners.Any(listener => !listener.IsActivated))
                throw new InvalidOperationException(
                    "Stratum listeners must be activated before the pool is announced online");

            logger.Info(() => $"Stratum ports {string.Join(", ", listeners.Select(x => $"{x.Endpoint.IPEndPoint.Address}:{x.Endpoint.IPEndPoint.Port}").ToArray())} online");

            using var registration = ct.Register(() =>
            {
                foreach(var listener in listeners)
                    listener.Dispose();
            });

            await Task.WhenAll(listeners.Select(x =>
                Listen(x.Socket, x.Endpoint, ct)));
        }

        finally
        {
            foreach(var listener in listeners)
                listener.Dispose();

            await DrainConnectionsAsync();
        }
    }

    internal static Socket CreateListenSocket(IPEndPoint endpoint)
    {
        Contract.RequiresNonNull(endpoint);

        var server = new Socket(endpoint.AddressFamily, SocketType.Stream,
            ProtocolType.Tcp);

        if(endpoint.Address.Equals(IPAddress.IPv6Any))
            server.DualMode = true;

        return server;
    }

    internal static Socket CreateBoundSocket(IPEndPoint endpoint)
    {
        var server = CreateListenSocket(endpoint);

        try
        {
            // .NET enables SO_REUSEADDR for TCP listeners by default. A retained Bind is
            // meaningful only when a second socket cannot bind the same endpoint while this
            // pool initializes, so disable address reuse before claiming the endpoint.
            server.SetSocketOption(SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress, false);

            if(OperatingSystem.IsWindows())
            {
                server.ExclusiveAddressUse = true;
                server.Bind(endpoint);
                return server;
            }

            return BindUnixSocketWithoutRuntimeAddressReuse(server, endpoint);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    internal static string ProbeNativeBindLibraryCandidates() =>
        NativeMethods.ProbeBindLibraryCandidates();

    internal static string[] GetLinuxNativeBindLibraryCandidates(
        Architecture architecture) =>
        NativeMethods.GetLinuxNativeLibraryCandidates(architecture);

    private static Socket BindUnixSocketWithoutRuntimeAddressReuse(Socket server,
        IPEndPoint endpoint)
    {
        // IPEndPoint.Serialize emits the current platform's native sockaddr layout and address
        // family values. It is therefore safe to pass this buffer directly to libc bind rather
        // than translating managed AddressFamily enum values by hand.
        var socketAddress = endpoint.Serialize();
        var addressBytes = socketAddress.Buffer.Span[..socketAddress.Size]
            .ToArray();
        var handle = server.SafeHandle;

        // Socket.Bind routes through the .NET Unix PAL, which enables SO_REUSEADDR for TCP
        // before the native bind. That permits two bound-but-not-listening sockets to claim the
        // same endpoint. Call the native bind directly, then reconstruct the managed wrapper so
        // IsBound, LocalEndPoint and accepted-socket endpoint state are populated from the handle.
        if(NativeMethods.Bind(handle, addressBytes,
               (uint) addressBytes.Length) != 0)
        {
            // Keep this throw immediately adjacent to Bind. SocketException() reads the
            // thread-local errno captured by the most recent SetLastError native call; any
            // intervening P/Invoke, including one reached through logging, can overwrite it and
            // silently disable AddressAlreadyInUse retry classification. The integer constructor
            // expects a managed socket error value rather than a native errno.
            throw new SocketException();
        }

        // Socket(SafeSocketHandle) retains the supplied handle; it does not remove ownership
        // from the original Socket. Give the descriptor a new owning handle and invalidate the
        // temporary owner before it can be finalized, so exactly one managed object owns the fd.
        var transferredHandle = new SafeSocketHandle(
            handle.DangerousGetHandle(), true);
        handle.SetHandleAsInvalid();
        GC.KeepAlive(server);

        try
        {
            return new Socket(transferredHandle);
        }
        catch
        {
            transferredHandle.Dispose();
            throw;
        }
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,
            SetLastError = true)]
        private delegate int BindDelegate(IntPtr socket,
            byte[] socketAddress, uint socketAddressLength);

        private static readonly Lazy<BindDelegate> bind = new(LoadBind,
            LazyThreadSafetyMode.ExecutionAndPublication);

        internal static int Bind(SafeSocketHandle socket,
            byte[] socketAddress, uint socketAddressLength)
        {
            var addedRef = false;

            try
            {
                socket.DangerousAddRef(ref addedRef);
                return bind.Value(socket.DangerousGetHandle(), socketAddress,
                    socketAddressLength);
            }
            finally
            {
                if(addedRef)
                    socket.DangerousRelease();
            }
        }

        private static BindDelegate LoadBind()
        {
            // dlsym on the main-program handle searches the process-global symbol scope on
            // supported Unix loaders. Prefer it so exclusivity does not depend on a glibc or
            // musl soname; retain explicit candidates for runtimes with narrower lookup rules.
            var mainProgram = NativeLibrary.GetMainProgramHandle();
            if(NativeLibrary.TryGetExport(mainProgram, "bind",
                   out var processBindAddress))
            {
                return Marshal.GetDelegateForFunctionPointer<BindDelegate>(
                    processBindAddress);
            }

            var candidates = GetNativeLibraryCandidates();

            foreach(var candidate in candidates)
            {
                if(!NativeLibrary.TryLoad(candidate, out var library))
                    continue;

                if(NativeLibrary.TryGetExport(library, "bind",
                       out var bindAddress))
                {
                    // Do not call NativeLibrary.Free for the successful handle. The delegate
                    // points into that library, so its native load reference must remain held
                    // for the process lifetime.
                    return Marshal.GetDelegateForFunctionPointer<BindDelegate>(
                        bindAddress);
                }

                NativeLibrary.Free(library);
            }

            throw new DllNotFoundException(
                $"Unable to load the native bind function required for exclusive Stratum listeners. Tried the process-global symbol scope and: {string.Join(", ", candidates)}");
        }

        internal static string ProbeBindLibraryCandidates()
        {
            var candidates = GetNativeLibraryCandidates();

            foreach(var candidate in candidates)
            {
                if(!NativeLibrary.TryLoad(candidate, out var library))
                    continue;

                try
                {
                    if(NativeLibrary.TryGetExport(library, "bind", out _))
                        return candidate;
                }
                finally
                {
                    NativeLibrary.Free(library);
                }
            }

            throw new DllNotFoundException(
                $"Unable to resolve the native bind fallback from: {string.Join(", ", candidates)}");
        }

        private static string[] GetNativeLibraryCandidates()
        {
            if(OperatingSystem.IsMacOS())
                return new[] { "libSystem.B.dylib" };

            if(OperatingSystem.IsFreeBSD())
                return new[] { "libc.so.7", "libc.so" };

            if(!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException(
                    "Exclusive native Stratum binding is supported only on Windows, Linux, macOS and FreeBSD");
            }

            return GetLinuxNativeLibraryCandidates(
                RuntimeInformation.ProcessArchitecture);
        }

        internal static string[] GetLinuxNativeLibraryCandidates(
            Architecture architecture)
        {
            var muslArchitectures = architecture switch
            {
                Architecture.X64 => new[] { "x86_64" },
                Architecture.X86 => new[] { "x86", "i386" },
                Architecture.Arm => new[] { "armhf", "armv7" },
                Architecture.Arm64 => new[] { "aarch64" },
                Architecture.S390x => new[] { "s390x" },
                Architecture.Ppc64le => new[] { "powerpc64le", "ppc64le" },
                Architecture.RiscV64 => new[] { "riscv64" },
                _ => new[] { architecture.ToString().ToLowerInvariant() },
            };

            // glibc and musl expose different sonames. Resolve explicitly instead of relying on
            // DllImport("libc"), which is not portable to Alpine/musl downstream images.
            return new[] { "libc.so.6" }
                .Concat(muslArchitectures.SelectMany(name => new[]
                {
                    $"libc.musl-{name}.so.1",
                    $"/lib/libc.musl-{name}.so.1",
                    $"ld-musl-{name}.so.1",
                    $"/lib/ld-musl-{name}.so.1",
                }))
                .ToArray();
        }
    }

    private async Task Listen(Socket server, StratumEndpoint port, CancellationToken ct)
    {
        var cert = GetTlsCert(port);

        while(!ct.IsCancellationRequested)
        {
            try
            {
                var socket = await server.AcceptAsync(ct);

                AcceptConnection(socket, port, cert, ct);
            }

            catch(OperationCanceledException)
            {
                // ignored
                break;
            }

            catch(ObjectDisposedException)
            {
                // ignored
                break;
            }

            catch(Exception ex)
            {
                logger.Error(ex);
            }
        }
    }

    private void AcceptConnection(Socket socket, StratumEndpoint port, X509Certificate2 cert, CancellationToken ct)
    {
        StratumConnection connection = null;
        var registered = false;
        var dispatched = false;

        Guard(() =>
        {
            var failStop = ctx.ResolveOptional<IMiningFailStopCoordinator>();

            if(failStop?.IsFailStopRequested == true)
            {
                StratumSocketCleanup.CloseAbortively(socket);
                return;
            }

            var remoteEndpoint = (IPEndPoint) socket.RemoteEndPoint;

            if(remoteEndpoint == null)
            {
                StratumSocketCleanup.CloseAbortively(socket);
                return;
            }

            // dispose of banned clients as early as possible
            if(DisconnectIfBanned(socket, remoteEndpoint))
                return;

            // init connection
            connection = new StratumConnection(logger, rmsm, clock,
                CreateConnectionId(),
                clusterConfig.Logging.GPDRCompliant, failStop?.Token ?? default);

            logger.Info(() => $"[{connection.ConnectionId}] Accepting connection from {remoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging.GPDRCompliant)}:{remoteEndpoint.Port} ...");

            RegisterConnection(connection);
            registered = true;
            OnConnect(connection, port.IPEndPoint);

            var dispatch = connection.DispatchAsync(socket, ct, port,
                remoteEndpoint, cert, OnRequestAsync, OnConnectionComplete,
                OnConnectionError);
            dispatched = true;

            if(!connectionTasks.TryAdd(connection.ConnectionId, dispatch))
            {
                // A previous dispatch with the same generated id may be between connection
                // unregistration and observer cleanup. The new dispatch owns its socket now;
                // terminate and observe it without removing the previous task's dictionary entry.
                connection.Disconnect();
                _ = ObserveUntrackedConnectionTaskAsync(
                    connection.ConnectionId, dispatch);
                throw new InvalidOperationException(
                    $"Connection task {connection.ConnectionId} is already tracked");
            }

            _ = ObserveConnectionTaskAsync(connection.ConnectionId, dispatch);
        }, ex =>
        {
            if(!dispatched)
            {
                StratumSocketCleanup.CloseAbortively(socket);

                if(registered)
                    UnregisterConnection(connection);
            }

            logger.Error(ex);
        });
    }

    private async Task ObserveUntrackedConnectionTaskAsync(string connectionId,
        Task dispatch)
    {
        try
        {
            await dispatch;
        }
        catch(Exception ex)
        {
            logger.Error(ex,
                "Unexpected failure while finalising untracked Stratum connection {0}",
                connectionId);
        }
    }

    private async Task ObserveConnectionTaskAsync(string connectionId,
        Task dispatch)
    {
        try
        {
            await dispatch;
        }
        catch(Exception ex)
        {
            // Dispatch reports connection errors through OnConnectionError. This observer exists
            // to keep the task rooted and guarantee removal even if a callback itself fails.
            logger.Error(ex,
                "Unexpected failure while finalising Stratum connection {0}",
                connectionId);
        }
        finally
        {
            try
            {
                await BeforeConnectionTaskRemovalAsync(connectionId);
            }
            catch(Exception ex)
            {
                logger.Error(ex,
                    "Unexpected failure before removing Stratum connection task {0}",
                    connectionId);
            }

            connectionTasks.TryRemove(connectionId, out _);
        }
    }

    private async Task DrainConnectionsAsync()
    {
        using var timeout = new CancellationTokenSource(ConnectionDrainTimeout);

        try
        {
            while(connectionTasks.Count > 0)
            {
                var pending = connectionTasks.Values.ToArray();

                if(pending.Length == 0)
                    continue;

                try
                {
                    await Task.WhenAll(pending).WaitAsync(timeout.Token);
                }
                catch(Exception ex) when(ex is not OperationCanceledException ||
                    !timeout.IsCancellationRequested)
                {
                    // Connection dispatch already reports its own failure. Continue draining
                    // the remaining tasks rather than allowing one fault to consume shutdown.
                    logger.Debug(ex, "A Stratum connection faulted while shutdown was draining it");
                }
            }
        }
        catch(OperationCanceledException) when(timeout.IsCancellationRequested)
        {
            var pending = connectionTasks.Count;
            var failStop = ctx.ResolveOptional<IMiningFailStopCoordinator>();
            failStop?.BeginFailStop(ProcessExitCodes.GeneralFailure);

            foreach(var connection in connections.Values)
            {
                try
                {
                    // The bounded graceful drain is exhausted. Abort remaining sockets so the
                    // process can exit and an exclusive listener can be reacquired safely.
                    connection.Disconnect();
                }
                catch(Exception ex) when(ex is IOException or
                    ObjectDisposedException)
                {
                    // A racing dispatch completion may already own disposal.
                }
            }

            logger.Fatal(
                "Timed out after {0} while draining {1} Stratum connection task(s). " +
                "Mining admission is closed and shutdown will continue so Share Recorder retains its recovery window.",
                ConnectionDrainTimeout, pending);
        }
    }

    protected void RegisterConnection(StratumConnection connection)
    {
        if(!connections.TryAdd(connection.ConnectionId, connection))
        {
            throw new InvalidOperationException(
                $"Connection id {connection.ConnectionId} is already registered");
        }

        PublishTelemetry(TelemetryCategory.Connections, TimeSpan.Zero, true, connections.Count);
    }

    protected void UnregisterConnection(StratumConnection connection)
    {
        if(!connections.TryRemove(connection.ConnectionId, out _))
        {
            throw new InvalidOperationException(
                $"Connection id {connection.ConnectionId} is not registered");
        }

        PublishTelemetry(TelemetryCategory.Connections, TimeSpan.Zero, true, connections.Count);
    }

    protected virtual string CreateConnectionId() =>
        CorrelationIdGenerator.GetNextId();

    // Test subclasses can widen the real completion window where connection removal precedes
    // removal of its socket-owning dispatch task. Production subclasses use the completed task.
    protected virtual Task BeforeConnectionTaskRemovalAsync(
        string connectionId) => Task.CompletedTask;

    protected abstract void OnConnect(StratumConnection connection, IPEndPoint portItem1);

    protected async Task OnRequestAsync(StratumConnection connection, JsonRpcRequest request, CancellationToken ct)
    {
        var failStop = ctx.ResolveOptional<IMiningFailStopCoordinator>();

        if(failStop?.IsFailStopRequested == true)
            throw new OperationCanceledException(
                "Stratum request rejected by the mining fail-stop gate",
                failStop.Token);

        // boot pre-connected clients
        if(banManager?.IsBanned(connection.RemoteEndpoint.Address) == true)
        {
            logger.Info(() => $"[{connection.ConnectionId}] Disconnecting banned client @ {connection.RemoteEndpoint.Address}");
            Disconnect(connection);
            return;
        }

        logger.Debug(() => $"[{connection.ConnectionId}] Dispatching request '{request.Method}' [{request.Id}]");

        var tsRequest = new Timestamped<JsonRpcRequest>(request, clock.Now);

        await OnRequestAsync(connection, tsRequest, ct);

        PublishTelemetry(TelemetryCategory.StratumRequest, request.Method, clock.Now - tsRequest.Timestamp);
    }

    /// <summary>
    /// Admits an accepted share to the accounting pipeline before its positive Stratum response.
    /// Both steps take concurrent healthy admissions against the exclusive mining fail-stop
    /// transition. A gate closure between them leaves the share published but deliberately
    /// unacknowledged; response queue admission itself is synchronous.
    /// </summary>
    protected async Task PublishShareAndAcknowledgeAsync(Share share,
        Func<Task> acknowledge, bool publishShare = true)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(acknowledge);

        var failStop = ctx.ResolveOptional<IMiningFailStopCoordinator>();

        if(failStop == null)
        {
            if(publishShare)
                messageBus.SendMessage(share);

            await share.PersistenceAdmission;
            await acknowledge();
            return;
        }

        using var acceptance = failStop.AcquireSubmissionAcceptance();

        if(publishShare)
        {
            // This is the sole production admission for a local Stratum share. MessageBus's
            // public share path owns an admission for relayed and internally generated shares;
            // entering it here as well would recurse when a persistence failure closes the gate.
            acceptance.PublishShare(messageBus, share);
        }

        // A normal bounded-queue admission completes immediately. Queue saturation transfers
        // ownership to the bounded emergency writer and completes only after its force-flush.
        // Merged mining publishes a statistical clone before returning this object and propagates
        // that clone's completion here. Deliberately wait outside the admission lock so storage
        // latency cannot delay an exclusive fail-stop transition.
        await share.PersistenceAdmission;

        Task response = null;
        acceptance.QueueResponse(() => response = acknowledge());
        await response;
    }

    protected void OnConnectionError(StratumConnection connection, Exception ex)
    {
        if(ex is AggregateException)
            ex = ex.InnerException;

        if(ex is IOException && ex.InnerException != null)
            ex = ex.InnerException;

        switch(ex)
        {
            case SocketException sockEx:
                if(!ignoredSocketErrors.Contains(sockEx.ErrorCode))
                    logger.Error(() => $"[{connection.ConnectionId}] Connection error: {ex}");
                break;

            case InvalidDataException idEx:
                logger.Error(() => $"[{connection.ConnectionId}] Connection error: {idEx}");
                break;

            case JsonException jsonEx:
                // junk received (invalid json)
                logger.Error(() => $"[{connection.ConnectionId}] Connection json error: {jsonEx.Message}");

                if(clusterConfig.Banning?.BanOnJunkReceive.HasValue == false || clusterConfig.Banning?.BanOnJunkReceive == true)
                {
                    logger.Info(() => $"[{connection.ConnectionId}] Banning client for sending junk");
                    banManager?.Ban(connection.RemoteEndpoint.Address, TimeSpan.FromMinutes(3));
                }
                break;

            case AuthenticationException authEx:
                // junk received (SSL handshake)
                logger.Error(() => $"[{connection.ConnectionId}] Connection json error: {authEx.Message}");

                if(clusterConfig.Banning?.BanOnJunkReceive.HasValue == false || clusterConfig.Banning?.BanOnJunkReceive == true)
                {
                    logger.Info(() => $"[{connection.ConnectionId}] Banning client for failing SSL handshake");
                    banManager?.Ban(connection.RemoteEndpoint.Address, TimeSpan.FromMinutes(3));
                }
                break;

            case IOException ioEx:
                // junk received (SSL handshake)
                logger.Error(() => $"[{connection.ConnectionId}] Connection json error: {ioEx.Message}");

                if(ioEx.Source == "System.Net.Security")
                {
                    if(clusterConfig.Banning?.BanOnJunkReceive.HasValue == false || clusterConfig.Banning?.BanOnJunkReceive == true)
                    {
                        logger.Info(() => $"[{connection.ConnectionId}] Banning client for failing SSL handshake");
                        banManager?.Ban(connection.RemoteEndpoint.Address, TimeSpan.FromMinutes(3));
                    }
                }
                break;

            case ObjectDisposedException:
                // socket disposed
                break;

            case ArgumentException argEx:
                if(argEx.TargetSite != streamWriterCtor || argEx.ParamName != "stream")
                    logger.Error(() => $"[{connection.ConnectionId}] Connection error: {ex}");
                break;

            case InvalidOperationException:
                // The source completed without providing data to receive
                break;

            default:
                logger.Error(() => $"[{connection.ConnectionId}] Connection error: {ex}");
                break;
        }

        UnregisterConnection(connection);
    }

    protected void OnConnectionComplete(StratumConnection connection)
    {
        var completion = connection.CompletionReason switch
        {
            StratumConnectionCompletionReason.PeerEof => "Received EOF",
            StratumConnectionCompletionReason.HostShutdown =>
                "Connection completed during host shutdown",
            StratumConnectionCompletionReason.MiningFailStop =>
                "Connection completed during mining fail-stop",
            StratumConnectionCompletionReason.IndependentCancellation =>
                "Connection completed after independent cancellation",
            _ => "Connection completed",
        };

        logger.Debug(() => $"[{connection.ConnectionId}] {completion}");

        UnregisterConnection(connection);
    }

    protected void Disconnect(StratumConnection connection)
    {
        Contract.RequiresNonNull(connection);

        connection.Disconnect();
    }

    private X509Certificate2 GetTlsCert(StratumEndpoint port)
    {
        lock(certs)
        {
            if(port.PoolEndpoint.Tls)
            {
                if(!certs.TryGetValue(port.PoolEndpoint.TlsPfxFile, out var cert))
                {
                    cert = Guard(() => X509CertificateLoader.LoadPkcs12FromFile(
                        port.PoolEndpoint.TlsPfxFile, port.PoolEndpoint.TlsPfxPassword), ex =>
                    {
                        logger.Info(() => $"Failed to load TLS certificate {port.PoolEndpoint.TlsPfxFile}: {ex.Message}");
                        throw ex;
                    });

                    certs.TryAdd(port.PoolEndpoint.TlsPfxFile, cert);
                }

                return cert;
            }
            else
                return null;
        }
    }

    private bool DisconnectIfBanned(Socket socket, IPEndPoint remoteEndpoint)
    {
        if(remoteEndpoint == null || banManager == null)
            return false;

        if(banManager.IsBanned(remoteEndpoint.Address))
        {
            logger.Debug(() => $"Disconnecting banned ip {remoteEndpoint.Address}");
            StratumSocketCleanup.CloseAbortively(socket);

            return true;
        }

        return false;
    }

    protected void PublishTelemetry(TelemetryCategory cat, TimeSpan elapsed, bool? success = null, int? total = null)
    {
        messageBus.SendTelemetry(poolConfig.Id, cat, elapsed, success, null, total);
    }

    protected void PublishTelemetry(TelemetryCategory cat, string info, TimeSpan elapsed, bool? success = null, int? total = null)
    {
        messageBus.SendTelemetry(poolConfig.Id, cat, info, elapsed, success, null, total);
    }

    protected abstract Task OnRequestAsync(StratumConnection connection, Timestamped<JsonRpcRequest> request, CancellationToken ct);
}
