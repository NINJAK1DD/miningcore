namespace Miningcore.Persistence.Model;

public sealed record PpsShareCredit
{
    public string PoolId { get; init; }
    public Guid AccountingId { get; init; }
    public string Address { get; init; }
    public decimal CalculatedAmount { get; init; }
    public double Difficulty { get; init; }
    public double NetworkDifficulty { get; init; }
    public long RewardBasisSatoshis { get; init; }
    public DateTime Created { get; init; }
}

public sealed record ShareAccountingBatch
{
    public Guid AccountingId { get; init; }
    public string PayloadHash { get; init; }
    public Share[] Shares { get; init; }
    public PpsShareCredit[] PpsCredits { get; init; }
    public DateTime Created { get; init; }
}

public enum ShareAccountingInsertResult
{
    Inserted,
    AlreadyCommitted,
}
