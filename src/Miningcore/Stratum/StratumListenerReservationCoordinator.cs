using System.Net;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Mining;
using NLog;

namespace Miningcore.Stratum;

internal sealed class StratumListenerReservation : IDisposable
{
    internal StratumListenerReservation(string poolId, StratumEndpoint endpoint,
        Socket socket)
    {
        PoolId = poolId ?? throw new ArgumentNullException(nameof(poolId));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    private int disposed;
    private int activated;

    internal string PoolId { get; }
    internal StratumEndpoint Endpoint { get; }
    internal Socket Socket { get; }
    internal bool IsActivated => Volatile.Read(ref activated) != 0;

    internal void Activate()
    {
        if(Interlocked.CompareExchange(ref activated, 1, 0) != 0)
            return;

        try
        {
            Socket.Listen();
        }
        catch
        {
            Volatile.Write(ref activated, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed, 1) == 0)
            Socket.Dispose();
    }
}

internal sealed class StratumListenerReservationSession : IDisposable
{
    internal StratumListenerReservationSession(
        Dictionary<string, StratumListenerReservation[]> listeners)
    {
        this.listeners = listeners ??
            throw new ArgumentNullException(nameof(listeners));
        Count = listeners.Values.Sum(x => x.Length);
    }

    private readonly object gate = new();
    private readonly Dictionary<string, StratumListenerReservation[]> listeners;
    private bool disposed;

    internal int Count { get; }

    internal StratumListenerReservation[] Claim(string poolId)
    {
        ArgumentException.ThrowIfNullOrEmpty(poolId);

        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if(!listeners.Remove(poolId, out var result))
            {
                throw new InvalidOperationException(
                    $"Stratum listener reservations for pool '{poolId}' are unavailable or were already claimed");
            }

            return result;
        }
    }

    public void Dispose()
    {
        StratumListenerReservation[] remaining;

        lock(gate)
        {
            if(disposed)
                return;

            disposed = true;
            remaining = listeners.Values.SelectMany(x => x).ToArray();
            listeners.Clear();
        }

        foreach(var listener in remaining)
            listener.Dispose();
    }
}

internal sealed class StratumListenerReservationCoordinator
{
    private sealed record PoolListenerPlan(string PoolId,
        StratumEndpoint[] Endpoints);

    internal static readonly TimeSpan AddressInUseRetryWindow =
        TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan InitialRetryDelay =
        TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaximumRetryDelay =
        TimeSpan.FromSeconds(5);

    internal StratumListenerReservationCoordinator(ILogger logger = null) :
        this(StratumServer.CreateBoundSocket, logger)
    {
    }

    internal StratumListenerReservationCoordinator(
        Func<IPEndPoint, Socket> reserveSocket, ILogger logger = null,
        TimeSpan? addressInUseRetryWindow = null,
        Func<TimeSpan, CancellationToken, Task> retryWait = null)
    {
        this.reserveSocket = reserveSocket ??
            throw new ArgumentNullException(nameof(reserveSocket));
        this.logger = logger;
        this.addressInUseRetryWindow = addressInUseRetryWindow ??
            AddressInUseRetryWindow;
        if(this.addressInUseRetryWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(addressInUseRetryWindow));

        this.retryWait = retryWait ?? Task.Delay;
    }

    private readonly Func<IPEndPoint, Socket> reserveSocket;
    private readonly ILogger logger;
    private readonly TimeSpan addressInUseRetryWindow;
    private readonly Func<TimeSpan, CancellationToken, Task> retryWait;

    internal async Task<StratumListenerReservationSession> ReserveAllAsync(
        IEnumerable<PoolConfig> pools, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pools);

        var reservations = new Dictionary<string,
            StratumListenerReservation[]>(StringComparer.Ordinal);
        var acquired = new List<StratumListenerReservation>();
        var activeIPv4Subnets = ListenerAddressUtils
            .CaptureActiveIPv4Subnets();
        var plans = PreflightListeners(pools, activeIPv4Subnets, ct);
        var retryDelayBudget = new AddressInUseRetryDelayBudget(
            addressInUseRetryWindow);

        try
        {
            foreach(var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                var poolReservations = new List<StratumListenerReservation>();

                foreach(var endpoint in plan.Endpoints)
                {
                    Socket socket = null;

                    try
                    {
                        socket = await ReserveSocketWithAddressInUseRetryAsync(
                            plan.PoolId, endpoint.IPEndPoint,
                            retryDelayBudget, ct);
                    }
                    catch(SocketException ex)
                    {
                        var retryDetail = ex.SocketErrorCode ==
                            SocketError.AddressAlreadyInUse &&
                            addressInUseRetryWindow > TimeSpan.Zero
                                ? $" after exhausting the shared {addressInUseRetryWindow.TotalSeconds:0.###}-second startup retry-delay budget"
                                : string.Empty;
                        throw new PoolStartupException(
                            $"Unable to reserve Stratum listener {FormatEndpoint(endpoint.IPEndPoint.Address, endpoint.IPEndPoint.Port)} for pool '{plan.PoolId}'{retryDetail}: socket error {ex.SocketErrorCode} (native error {ex.NativeErrorCode}): {ex.Message}",
                            plan.PoolId, ex);
                    }
                    catch(OperationCanceledException) when(ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch(Exception ex)
                    {
                        throw new PoolStartupException(
                            $"Unable to reserve Stratum listener {FormatEndpoint(endpoint.IPEndPoint.Address, endpoint.IPEndPoint.Port)} for pool '{plan.PoolId}': {ex.GetType().Name}: {ex.Message}",
                            plan.PoolId, ex);
                    }

                    StratumListenerReservation reservation = null;
                    var acquiredOwnsReservation = false;

                    try
                    {
                        reservation = new StratumListenerReservation(
                            plan.PoolId, endpoint, socket);
                        socket = null;

                        // Register the owner used by the outer rollback before
                        // exposing the reservation through the per-pool plan.
                        acquired.Add(reservation);
                        acquiredOwnsReservation = true;
                        poolReservations.Add(reservation);
                    }
                    finally
                    {
                        // Preserve ownership across every exception boundary:
                        // either the raw socket, the reservation, or acquired owns
                        // the handle when this block exits.
                        socket?.Dispose();
                        if(!acquiredOwnsReservation)
                            reservation?.Dispose();
                    }
                }

                reservations.Add(plan.PoolId,
                    poolReservations.ToArray());
            }

            return new StratumListenerReservationSession(reservations);
        }
        catch
        {
            foreach(var listener in acquired)
                listener.Dispose();

            throw;
        }
    }

    private static PoolListenerPlan[] PreflightListeners(
        IEnumerable<PoolConfig> pools,
        IReadOnlyCollection<ListenerAddressUtils.IPv4InterfaceSubnet>
            activeIPv4Subnets,
        CancellationToken ct)
    {
        var selected = pools.Where(pool => pool?.Enabled == true &&
                pool.EnableInternalStratum == true)
            .ToArray();
        var poolIds = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<PoolListenerPlan>(selected.Length);

        // This lower-level boundary independently validates selected pool identity,
        // endpoint objects, literal addresses and host suitability before invoking
        // the reservation delegate. Full live configuration validation, including
        // the non-zero port range, remains PoolConfigValidator's responsibility;
        // port zero is intentionally useful to low-level socket tests.
        foreach(var pool in selected)
        {
            ct.ThrowIfCancellationRequested();

            if(string.IsNullOrEmpty(pool.Id))
            {
                throw new PoolStartupException(
                    "Unable to reserve Stratum listeners: pool id missing or empty",
                    pool.Id);
            }

            if(!poolIds.Add(pool.Id))
            {
                throw new PoolStartupException(
                    $"Unable to reserve Stratum listeners: duplicate pool id '{pool.Id}'",
                    pool.Id);
            }

            var endpoints = new List<StratumEndpoint>();

            foreach(var (port, poolEndpoint) in pool.Ports ??
                new Dictionary<int, PoolEndpoint>())
            {
                ct.ThrowIfCancellationRequested();

                if(poolEndpoint == null)
                {
                    throw new PoolStartupException(
                        ListenerAddressUtils.FormatNullEndpointError(
                            pool.Id, port), pool.Id);
                }

                if(!ListenerAddressUtils.TryResolve(
                       poolEndpoint.ListenAddress, out var address))
                {
                    throw new PoolStartupException(
                        $"Pool '{pool.Id}' Stratum port {port}: invalid listen address '{poolEndpoint.ListenAddress}'",
                        pool.Id);
                }

                if(!ListenerAddressUtils.IsSuitableForListener(address,
                       activeIPv4Subnets, out var reason))
                {
                    throw new PoolStartupException(
                        $"Pool '{pool.Id}' Stratum endpoint {FormatEndpoint(address, port)} cannot be reserved: {reason}",
                        pool.Id);
                }

                endpoints.Add(new StratumEndpoint(
                    new IPEndPoint(address, port), poolEndpoint));
            }

            plans.Add(new PoolListenerPlan(pool.Id, endpoints.ToArray()));
        }

        return plans.ToArray();
    }

    private async Task<Socket> ReserveSocketWithAddressInUseRetryAsync(
        string poolId, IPEndPoint endpoint,
        AddressInUseRetryDelayBudget retryDelayBudget,
        CancellationToken ct)
    {
        // A fresh endpoint gets a short first retry even when earlier endpoints consumed part of
        // the shared delay budget. This keeps transient recovery responsive without multiplying
        // the total scheduled wait allowance by the number of listeners.
        var delay = InitialRetryDelay;

        while(true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return reserveSocket(endpoint);
            }
            // On Windows, a live exclusive owner (including a dual-stack collision) may report
            // AccessDenied and must fail fast. Residual TIME_WAIT reports AddressAlreadyInUse,
            // which is the only transient ownership state this bounded retry is designed for.
            catch(SocketException ex) when(
                ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                if(!retryDelayBudget.TryTake(delay, out var wait))
                    throw;

                logger?.Warn(() =>
                    $"Stratum listener {FormatEndpoint(endpoint.Address, endpoint.Port)} for pool '{poolId}' is still in use; retrying in {wait.TotalSeconds:0.###} seconds with {retryDelayBudget.Remaining.TotalSeconds:0.###} seconds left in the shared startup retry-delay budget");
                await retryWait(wait, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    delay.TotalMilliseconds * 2,
                    MaximumRetryDelay.TotalMilliseconds));
            }
        }
    }

    // ReserveAllAsync consumes this mutable budget sequentially. Do not parallelize endpoint
    // reservation or share this instance across tasks without adding synchronization.
    private sealed class AddressInUseRetryDelayBudget
    {
        internal AddressInUseRetryDelayBudget(TimeSpan remaining)
        {
            this.remaining = remaining;
        }

        private TimeSpan remaining;

        internal TimeSpan Remaining => remaining;

        internal bool TryTake(TimeSpan requested, out TimeSpan wait)
        {
            if(remaining <= TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
                return false;
            }

            wait = requested <= remaining ? requested : remaining;
            remaining -= wait;
            return true;
        }
    }

    private static string FormatEndpoint(IPAddress address, int port) =>
        $"{ListenerAddressUtils.FormatHost(address)}:{port}";
}
