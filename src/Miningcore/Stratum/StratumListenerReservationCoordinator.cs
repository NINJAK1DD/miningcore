using System.Net;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Mining;

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
    internal StratumListenerReservationCoordinator() :
        this(StratumServer.CreateBoundSocket)
    {
    }

    internal StratumListenerReservationCoordinator(
        Func<IPEndPoint, Socket> reserveSocket)
    {
        this.reserveSocket = reserveSocket ??
            throw new ArgumentNullException(nameof(reserveSocket));
    }

    private readonly Func<IPEndPoint, Socket> reserveSocket;

    internal StratumListenerReservationSession ReserveAll(
        IEnumerable<PoolConfig> pools)
    {
        ArgumentNullException.ThrowIfNull(pools);

        var reservations = new Dictionary<string,
            StratumListenerReservation[]>(StringComparer.Ordinal);
        var acquired = new List<StratumListenerReservation>();

        try
        {
            foreach(var pool in pools.Where(pool => pool?.Enabled == true &&
                        pool.EnableInternalStratum == true))
            {
                var poolReservations = new List<StratumListenerReservation>();

                foreach(var (port, poolEndpoint) in pool.Ports ??
                    new Dictionary<int, PoolEndpoint>())
                {
                    if(!ListenerAddressUtils.TryResolve(
                           poolEndpoint?.ListenAddress, out var address))
                    {
                        throw new PoolStartupException(
                            $"Pool '{pool.Id}' Stratum port {port}: invalid listen address '{poolEndpoint?.ListenAddress}'",
                            pool.Id);
                    }

                    if(!ListenerAddressUtils.IsSuitableForListener(address,
                           out var reason))
                    {
                        throw new PoolStartupException(
                            $"Pool '{pool.Id}' Stratum endpoint {FormatEndpoint(address, port)} cannot be reserved: {reason}",
                            pool.Id);
                    }

                    var endpoint = new StratumEndpoint(
                        new IPEndPoint(address, port), poolEndpoint);
                    Socket socket;

                    try
                    {
                        socket = reserveSocket(endpoint.IPEndPoint);
                    }
                    catch(SocketException ex)
                    {
                        throw new PoolStartupException(
                            $"Unable to reserve Stratum listener {FormatEndpoint(address, port)} for pool '{pool.Id}': socket error {ex.SocketErrorCode} (native error {ex.NativeErrorCode}): {ex.Message}",
                            pool.Id, ex);
                    }

                    var reservation = new StratumListenerReservation(
                        pool.Id, endpoint, socket);
                    poolReservations.Add(reservation);
                    acquired.Add(reservation);
                }

                reservations.Add(pool.Id, poolReservations.ToArray());
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

    private static string FormatEndpoint(IPAddress address, int port) =>
        $"{ListenerAddressUtils.FormatHost(address)}:{port}";
}
