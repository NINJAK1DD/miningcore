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
    private const string StoppedReason = "stopped";
    private static readonly Counter decisions = Metrics.CreateCounter(
        "miningcore_stratum_connections_admission_total", "Stratum connection admission decisions",
        new CounterConfiguration { LabelNames = new[] { "pool", "reason" } });
    private static readonly Gauge active = Metrics.CreateGauge(
        "miningcore_stratum_admission_active", "Stratum dispatches retaining admission capacity",
        new GaugeConfiguration { LabelNames = new[] { "pool" } });
    private static readonly Gauge tracked = Metrics.CreateGauge(
        "miningcore_stratum_admission_addresses", "Retained Stratum admission identities",
        new GaugeConfiguration { LabelNames = new[] { "pool" } });
    private static readonly Gauge pending = Metrics.CreateGauge(
        "miningcore_stratum_admission_pending_identities", "Dispatches awaiting trusted proxy identity",
        new GaugeConfiguration { LabelNames = new[] { "pool" } });
    private static readonly Gauge addressActive = Metrics.CreateGauge(
        "miningcore_stratum_admission_max_address_active", "Largest concurrent dispatch count for any one identity",
        new GaugeConfiguration { LabelNames = new[] { "pool" } });
    private static readonly Gauge limits = Metrics.CreateGauge(
        "miningcore_stratum_admission_limit", "Configured admission limits; zero after pool shutdown",
        new GaugeConfiguration { LabelNames = new[] { "pool", "limit" } });
    private readonly object gate = new();
    private readonly Dictionary<IPAddress, AddressState> addresses = new();
    private readonly LinkedList<AddressState> idle = new();
    private readonly StratumAdmissionConfig config;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly string poolId;
    private readonly Gauge.Child activeMetric;
    private readonly Gauge.Child trackedMetric;
    private readonly Gauge.Child pendingMetric;
    private readonly Gauge.Child addressActiveMetric;
    private readonly List<Gauge.Child> limitMetrics = new();
    private readonly Dictionary<int, int> occupancyCounts = new();
    private readonly SortedSet<int> occupancies = new();
    private double tokens;
    private long updated;
    private WarningBudget refusalWarnings;
    private WarningBudget advisoryWarnings;
    private int count;
    private int pendingCount;
    private bool stopped;

    internal StratumConnectionAdmission(StratumAdmissionConfig config, TimeProvider time,
        string poolId, ILogger logger)
    {
        config = config.Snapshot();
        new StratumAdmissionConfigValidator().ValidateAndThrow(config);
        this.config = config;
        this.time = time;
        this.poolId = poolId;
        this.logger = logger;
        tokens = config.Burst;
        updated = time.GetTimestamp();
        activeMetric = active.WithLabels(poolId);
        trackedMetric = tracked.WithLabels(poolId);
        pendingMetric = pending.WithLabels(poolId);
        addressActiveMetric = addressActive.WithLabels(poolId);
        SetLimit("connectionsPerSecond", config.ConnectionsPerSecond);
        SetLimit("burst", config.Burst);
        SetLimit("maxConcurrentConnections", config.MaxConcurrentConnections);
        SetLimit("maxPendingIdentities", config.MaxPendingIdentities);
        SetLimit("connectionsPerSecondPerAddress", config.ConnectionsPerSecondPerAddress);
        SetLimit("burstPerAddress", config.BurstPerAddress);
        SetLimit("maxConcurrentConnectionsPerAddress", config.MaxConcurrentConnectionsPerAddress);
        SetLimit("maxTrackedAddresses", config.MaxTrackedAddresses);
        SetLimit("idleExpirySeconds", config.IdleExpirySeconds);
        SetLimit("startupTimeoutSeconds", config.StartupTimeoutSeconds);
        logger.Info("Stratum admission: {0}/s burst {1}, {2} concurrent; per address {3}/s burst {4}, {5} concurrent; {6} pending identities, {7} tracked addresses, {8}s idle retention, {9}s startup timeout",
            config.ConnectionsPerSecond, config.Burst, config.MaxConcurrentConnections,
            config.ConnectionsPerSecondPerAddress, config.BurstPerAddress, config.MaxConcurrentConnectionsPerAddress,
            config.MaxPendingIdentities, config.MaxTrackedAddresses, config.IdleExpirySeconds, config.StartupTimeoutSeconds);
    }

    private void SetLimit(string name, int value)
    {
        var metric = limits.WithLabels(poolId, name);
        metric.Set(value);
        limitMetrics.Add(metric);
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
            if(stopped) return Reject(StoppedReason, now);
            Sweep(now, 16);
            Refill(ref tokens, ref updated, now, config.Burst, config.ConnectionsPerSecond);
            if(count >= config.MaxConcurrentConnections)
                return Reject("pool-concurrency", now);
            if(proxy && pendingCount >= config.MaxPendingIdentities)
                return Reject("pending-identity-capacity", now);
            if(tokens < 1)
                return Reject("pool-rate", now);

            AddressState identity = null;
            if(!proxy && !TryIdentity(address, now, out identity))
                return false;
            // Direct address refusals do no setup and must not drain other clients' pool
            // allowance. Trusted proxies pay now: their identity is only known after setup.
            tokens--;
            count++;
            if(proxy) pendingMetric.Set(++pendingCount);
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
        ChangeOccupancy(state.Active, state.Active + 1);
        state.Active++;
        if((long) state.Active * 5 >= (long) config.MaxConcurrentConnectionsPerAddress * 4)
            LogSummary("address-near-capacity; check fleet sizing and NAT/proxy identity configuration", now, ref advisoryWarnings);
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
        // Expected teardown refusals remain observable without warning about routine
        // shutdown or consuming the operator-actionable refusal summary budget.
        if(reason != StoppedReason)
            LogSummary(reason, now, ref refusalWarnings);
        return false;
    }

    private struct WarningBudget
    {
        internal long LastLog;
        internal bool Logged;
        internal long Suppressed;
    }

    private void LogSummary(string reason, long now, ref WarningBudget budget)
    {
        if(!budget.Logged || time.GetElapsedTime(budget.LastLog, now) >= TimeSpan.FromMinutes(1))
        {
            logger.Warn("Stratum connection admission: {0}; suppressed since last category summary: {1}. Existing connections are retained.", reason, budget.Suppressed);
            budget.Logged = true;
            budget.LastLog = now;
            budget.Suppressed = 0;
        }
        else
            budget.Suppressed++;
    }

    // An exact maximum without scanning the identity table or exporting client labels.
    private void ChangeOccupancy(int previous, int current)
    {
        if(previous > 0 && --occupancyCounts[previous] == 0)
        {
            occupancyCounts.Remove(previous);
            occupancies.Remove(previous);
        }
        if(current > 0)
        {
            occupancyCounts.TryGetValue(current, out var existing);
            occupancyCounts[current] = existing + 1;
            occupancies.Add(current);
        }
        addressActiveMetric.Set(occupancies.Count == 0 ? 0 : occupancies.Max);
    }

    internal void RecordStartupTimeout() => decisions.WithLabels(poolId, "startup-timeout").Inc();

    internal void Stop()
    {
        lock(gate)
        {
            stopped = true;
            foreach(var metric in limitMetrics) metric.Set(0);
            ClearStoppedState();
        }
    }

    private void ClearStoppedState()
    {
        // A timed-out server drain can still own accounting work. Keep truthful active
        // gauges until its final lease exits, then discard all idle identities as well.
        if(!stopped || count != 0) return;
        addresses.Clear();
        idle.Clear();
        trackedMetric.Set(0);
        activeMetric.Set(0);
        pendingMetric.Set(0);
        addressActiveMetric.Set(0);
    }

    internal void SweepIdle()
    {
        lock(gate)
            if(!stopped) Sweep(time.GetTimestamp(), 256);
    }

    // The conservative sizing invariant keeps normal traffic below the defensive
    // full-ledger guard. Tests can construct that boundary without exposing mutable
    // collections or weakening configuration validation. No live ownership is replaced.
    internal void SeedIdleIdentitiesForTesting(IReadOnlyCollection<IPAddress> identities)
    {
        lock(gate)
        {
            if(stopped || count != 0 || addresses.Count != 0 || identities.Count > config.MaxTrackedAddresses)
                throw new InvalidOperationException("Idle identities require an empty, running admission controller and must fit its capacity");
            var normalized = identities.Select(Normalize).ToArray();
            if(normalized.Distinct().Count() != normalized.Length)
                throw new ArgumentException("Idle identities must be distinct after normalization", nameof(identities));

            var now = time.GetTimestamp();
            foreach(var address in normalized)
            {
                var state = new AddressState(address, config.BurstPerAddress, now) { IdleSince = now };
                state.IdleNode = idle.AddLast(state);
                addresses.Add(address, state);
            }
            trackedMetric.Set(addresses.Count);
        }
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
                if(owner.stopped) return owner.Reject(StoppedReason, owner.time.GetTimestamp());
                if(identity != null) return identity.Address.Equals(Normalize(address));
                if(!owner.TryIdentity(address, owner.time.GetTimestamp(), out var admitted)) return false;
                identity = admitted;
                owner.pendingMetric.Set(--owner.pendingCount);
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
                if(identity == null)
                    owner.pendingMetric.Set(--owner.pendingCount);
                else
                {
                    owner.ChangeOccupancy(identity.Active, identity.Active - 1);
                    if(--identity.Active == 0)
                    {
                        identity.IdleSince = owner.time.GetTimestamp();
                        identity.IdleNode = owner.idle.AddLast(identity);
                    }
                }
                owner.ClearStoppedState();
            }
        }
    }
}
