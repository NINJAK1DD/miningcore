namespace Miningcore.Persistence.Model;

public class Block
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public ulong BlockHeight { get; set; }
    public double NetworkDifficulty { get; set; }
    public BlockStatus Status { get; set; }
    public string Type { get; set; }
    public double ConfirmationProgress { get; set; }
    public double? Effort { get; set; }
    public double? MinerEffort { get; set; }
    public string TransactionConfirmationData { get; set; }
    public string Miner { get; set; }
    public decimal Reward { get; set; }
    public string Source { get; set; }
    public string Hash { get; set; }
    public DateTime Created { get; set; }

    /// <summary>
    /// Runtime-only signal used by payout classification when an unresolved
    /// block candidate has just become a proven accepted block. Repository
    /// mappings ignore this property.
    /// </summary>
    public bool NotifyBlockFoundOnUpdate { get; set; }
}
