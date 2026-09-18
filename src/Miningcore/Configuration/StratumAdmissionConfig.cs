namespace Miningcore.Configuration;

/// <summary>Per-pool limits shared by all internal Stratum listeners. No admission queue or IP bans.</summary>
public class StratumAdmissionConfig
{
    public int ConnectionsPerSecond { get; set; } = 100;
    public int Burst { get; set; } = 200;
    public int MaxConcurrentConnections { get; set; } = 4096;
    public int MaxPendingIdentities { get; set; } = 256;
    public int ConnectionsPerSecondPerAddress { get; set; } = 2;
    public int BurstPerAddress { get; set; } = 32;
    public int MaxConcurrentConnectionsPerAddress { get; set; } = 256;
    public int MaxTrackedAddresses { get; set; } = 32768;
    public int IdleExpirySeconds { get; set; } = 120;
    public int StartupTimeoutSeconds { get; set; } = 10;

    // Include a full pool burst as well as arrivals during idle retention. A lease
    // already alive at the start of that interval is covered by the concurrency cap.
    internal long MinimumTrackedAddresses => (long) MaxConcurrentConnections + Burst +
        (long) ConnectionsPerSecond * IdleExpirySeconds;

    // All members are value types. Any future reference-valued setting must be
    // deep-copied here before the snapshot can be treated as an immutable policy.
    internal StratumAdmissionConfig Snapshot() => (StratumAdmissionConfig) MemberwiseClone();
}
