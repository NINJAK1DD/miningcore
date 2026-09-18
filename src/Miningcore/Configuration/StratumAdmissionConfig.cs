namespace Miningcore.Configuration;

/// <summary>Per-pool limits shared by all internal Stratum listeners. No admission queue or IP bans.</summary>
public class StratumAdmissionConfig
{
    public int ConnectionsPerSecond { get; set; } = 100;
    public int Burst { get; set; } = 200;
    public int MaxConcurrentConnections { get; set; } = 4096;
    public int ConnectionsPerSecondPerAddress { get; set; } = 2;
    public int BurstPerAddress { get; set; } = 32;
    public int MaxConcurrentConnectionsPerAddress { get; set; } = 256;
    public int MaxTrackedAddresses { get; set; } = 16384;
    public int IdleExpirySeconds { get; set; } = 120;
    public int StartupTimeoutSeconds { get; set; } = 10;
}
