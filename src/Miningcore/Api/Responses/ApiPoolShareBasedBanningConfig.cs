namespace Miningcore.Api.Responses;

/// <summary>
/// Public projection of share-based banning policy. Keeping this contract
/// separate from runtime configuration prevents internal-only additions from
/// becoming API fields implicitly.
/// </summary>
public class ApiPoolShareBasedBanningConfig
{
    public bool Enabled { get; set; }
    public int CheckThreshold { get; set; }
    public double InvalidPercent { get; set; }
    public int Time { get; set; }
    public double? MinerEffortPercent { get; set; }
    public int? MinerEffortTime { get; set; }
}
