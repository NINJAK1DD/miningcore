using System.Collections.Frozen;
using System.Net;
using System.Runtime.CompilerServices;
using Miningcore.Configuration;

namespace Miningcore.Stratum;

// Ban keys are shared across pools, so automatic-ban protection must cover every
// enabled listener before the first pool starts. Scope the immutable snapshot to
// the cluster configuration, rather than leaking trust across host lifetimes.
internal sealed class StratumClusterProxyPolicy
{
    private static readonly ConditionalWeakTable<ClusterConfig, StratumClusterProxyPolicy> snapshots = new();
    private readonly FrozenDictionary<PoolEndpoint, StratumProxyPolicy> listeners;
    private readonly FrozenSet<IPAddress> trusted;

    private StratumClusterProxyPolicy(ClusterConfig config)
    {
        listeners = (config.Pools ?? Array.Empty<PoolConfig>())
            .Where(pool => pool.Enabled && pool.EnableInternalStratum == true && pool.Ports != null)
            .SelectMany(pool => pool.Ports.Values).Distinct()
            .ToFrozenDictionary(endpoint => endpoint, endpoint => new StratumProxyPolicy(endpoint.TcpProxyProtocol));
        trusted = listeners.Values.SelectMany(policy => policy.TrustedPeers).ToFrozenSet();
    }

    internal static StratumClusterProxyPolicy For(ClusterConfig config) =>
        snapshots.GetValue(config, value => new StratumClusterProxyPolicy(value));

    internal StratumProxyPolicy ForListener(PoolEndpoint endpoint)
    {
        if(listeners.TryGetValue(endpoint, out var policy))
            return policy;
        // Standalone servers may have no cluster listener inventory. They cannot
        // enable proxy trust outside the cluster-wide automatic-ban protection.
        if(endpoint.TcpProxyProtocol?.Enable == true)
            throw new InvalidOperationException("PROXY listener is absent from the startup cluster trust snapshot");
        return new StratumProxyPolicy(null);
    }

    internal bool IsTrustedProxy(IPAddress address) =>
        trusted.Contains(StratumConnectionAdmission.Normalize(address));
}
