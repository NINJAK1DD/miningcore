namespace Miningcore.Persistence.Postgres.Entities;

public class Share
{
    public string PoolId { get; set; }
    public long BlockHeight { get; set; }
    public string Miner { get; set; }
    public string Worker { get; set; }
    public string UserAgent { get; set; }
    public double Difficulty { get; set; }
    public double? ShareDifficulty { get; set; }
    public double? ActualDifficulty { get; set; }
    public double NetworkDifficulty { get; set; }
    public string IpAddress { get; set; }
    public string Source { get; set; }
    public string SessionId { get; set; }
    public Guid? AccountingId { get; set; }
    public short? AccountingRole { get; set; }
    public long? RewardBasisSatoshis { get; set; }
    public DateTime Created { get; set; }
}
