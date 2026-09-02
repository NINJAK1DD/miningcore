namespace Miningcore.Persistence.Postgres.Entities;

public class Block
{
    public long Id { get; set; }
    public string PoolId { get; set; }
    public long BlockHeight { get; set; }
    public double NetworkDifficulty { get; set; }
    public string Status { get; set; }
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
    public string SettlementMode { get; set; }
    public long? GrossRewardSatoshis { get; set; }
    public long? DirectMinerRewardSatoshis { get; set; }
    public string DirectMinerScriptPubKey { get; set; }
    public string DirectRecipientOutputs { get; set; }
    public DateTime? DirectSettlementLastChecked { get; set; }
    public string DirectSubmissionState { get; set; }
    public string DirectSubmissionBlock { get; set; }
    public int? DirectSubmissionAttempts { get; set; }
    public int? DirectSubmissionDefinitiveMisses { get; set; }
    public DateTime? DirectSubmissionLastAttempt { get; set; }
}
