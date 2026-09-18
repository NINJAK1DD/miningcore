using System.Net;
using FluentValidation;
using Miningcore.Configuration;
using NLog;
using Prometheus;

namespace Miningcore.Stratum;

// One instance per pool, shared across its listeners. All mutations (including timestamps) are
// serialized. The idle list contains exactly one node per inactive identity, never stale leases.
internal sealed class StratumConnectionAdmission
{
    private static readonly Counter decisions = Metrics.CreateCounter(
        "miningcore_stratum_connections_admission_total", "Stratum connection admission decisions",
        new CounterConfiguration { LabelNames = new[] { "pool", "reason" } });
    private static readonly Gauge active = Metrics.CreateGauge(
        "miningcore_stratum_admission_active", "Stratum dispatches retaining admission capacity",
        new GaugeConfiguration { LabelNames = new[] { "pool" } });
    private static readonly Gauge tracked = Metrics.CreateGauge(
        "miningcore_stratum_admission_addresses", "Retained Stratum admission identities",
        new GaugeConfiguration { LabelNames = new[] { "pool" } });
    private readonly object gate = new();
    private readonly Dictionary<IPAddress, AddressState> addresses = new();
    private readonly LinkedList<AddressState> idle = new();
    private readonly StratumAdmissionConfig config;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly string poolId;
    private readonly Gauge.Child activeMetric;
    private readonly Gauge.Child trackedMetric;
    private double tokens;
    private long updated;
    private long lastLog;
    private bool logged;
    private long suppressed;
    private int count;

    internal StratumConnectionAdmission(StratumAdmissionConfig config, TimeProvider time,
        string poolId, ILogger logger)
    {
        new StratumAdmissionConfigValidator().ValidateAndThrow(config);
        this.config = config;
        this.time = time;
        this.poolId = poolId;
        this.logger = logger;
        tokens = config.Burst;
        updated = time.GetTimestamp();
        activeMetric = active.WithLabels(poolId);
        trackedMetric = tracked.WithLabels(poolId);
    }

    internal (int Active, int Addresses) Snapshot
    {
        get { lock(gate) return (count, addresses.Count); }
    }

    internal bool TryAcquire(IPAddress address, bool proxy, out Lease lease)
    {
        lock(gate)
        {
            lease = null;
            var now = time.GetTimestamp();
            Sweep(now, 16);
            Refill(ref tokens, ref updated, now, config.Burst, config.ConnectionsPerSecond);
            if(count >= config.MaxConcurrentConnections)
                return Reject("pool-concurrency", now);
            if(tokens < 1)
                return Reject("pool-rate", now);

            AddressState identity = null;
            if(!proxy && !TryIdentity(address, now, out identity))
                return false;
            // Direct address refusals do no setup and must not drain other clients' pool
            // allowance. Trusted proxies pay now: their identity is only known after setup.
            tokens--;
            count++;
            activeMetric.Set(count);
            decisions.WithLabels(poolId, "transport-admitted").Inc();
            lease = new Lease(this, identity);
            return true;
        }
    }

    private bool TryIdentity(IPAddress address, long now, out AddressState state)
    {
        address = Normalize(address);
        if(!addresses.TryGetValue(address, out state))
        {
            if(addresses.Count >= config.MaxTrackedAddresses)
                return Reject("address-capacity", now);
            state = new AddressState(address, config.BurstPerAddress, now);
            addresses.Add(address, state);
            trackedMetric.Set(addresses.Count);
        }

        Refill(ref state.Tokens, ref state.Updated, now,
            config.BurstPerAddress, config.ConnectionsPerSecondPerAddress);
        if(state.Active >= config.MaxConcurrentConnectionsPerAddress)
            return Reject("address-concurrency", now);
        if(state.Tokens < 1)
            return Reject("address-rate", now);

        state.Tokens--;
        state.Active++;
        if(state.IdleNode != null)
        {
            idle.Remove(state.IdleNode);
            state.IdleNode = null;
        }
        decisions.WithLabels(poolId, "identity-admitted").Inc();
        return true;
    }

    private void Refill(ref double available, ref long previous, long now, int capacity, int rate)
    {
        available = Math.Min(capacity, available + Math.Max(0, time.GetElapsedTime(previous, now).TotalSeconds) * rate);
        previous = now;
    }

    private bool Reject(string reason, long now)
    {
        decisions.WithLabels(poolId, reason).Inc();
        if(!logged || time.GetElapsedTime(lastLog, now) >= TimeSpan.FromMinutes(1))
        {
            logger.Warn("Stratum connection admission refused: {0}; suppressed since last summary: {1}. Existing connections are retained.", reason, suppressed);
            logged = true;
            lastLog = now;
            suppressed = 0;
        }
        else
            suppressed++;
        return false;
    }

    internal void SweepIdle()
    {
        lock(gate) Sweep(time.GetTimestamp(), 256);
    }

    private void Sweep(long now, int limit)
    {
        while(limit-- > 0 && idle.First is { } first &&
            time.GetElapsedTime(first.Value.IdleSince, now).TotalSeconds >= config.IdleExpirySeconds)
        {
            addresses.Remove(first.Value.Address);
            idle.RemoveFirst();
        }
        trackedMetric.Set(addresses.Count);
    }

    internal static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal sealed class AddressState(IPAddress address, double tokens, long now)
    {
        internal readonly IPAddress Address = address;
        internal double Tokens = tokens;
        internal long Updated = now;
        internal int Active;
        internal long IdleSince;
        internal LinkedListNode<AddressState> IdleNode;
    }

    internal sealed class Lease : IDisposable
    {
        private readonly StratumConnectionAdmission owner;
        private AddressState identity;
        private bool disposed;

        internal Lease(StratumConnectionAdmission owner, AddressState identity)
        {
            this.owner = owner;
            this.identity = identity;
        }

        internal bool TrySetIdentity(IPAddress address)
        {
            lock(owner.gate)
            {
                if(disposed) return false;
                if(identity != null) return identity.Address.Equals(Normalize(address));
                if(!owner.TryIdentity(address, owner.time.GetTimestamp(), out var admitted)) return false;
                identity = admitted;
                return true;
            }
        }

        public void Dispose()
        {
            lock(owner.gate)
            {
                if(disposed) return;
                disposed = true;
                owner.count--;
                owner.activeMetric.Set(owner.count);
                if(identity != null && --identity.Active == 0)
                {
                    identity.IdleSince = owner.time.GetTimestamp();
                    identity.IdleNode = owner.idle.AddLast(identity);
                }
            }
        }
    }
}
