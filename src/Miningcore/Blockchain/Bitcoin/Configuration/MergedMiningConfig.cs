namespace Miningcore.Blockchain.Bitcoin.Configuration;

/// <summary>
/// Configures auxiliary proof-of-work mining from a Bitcoin-family parent pool.
/// </summary>
public class MergedMiningConfig
{
    /// <summary>
    /// Enables merged mining for the parent pool.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Miningcore pool id of the auxiliary chain, for example "dogecoin".
    /// </summary>
    public string AuxPoolId { get; set; }

    /// <summary>
    /// Password control variable used by miners to supply their auxiliary payout address.
    /// Example: d=65536;doge=DOGE_ADDRESS
    /// </summary>
    public string AddressParameter { get; set; } = "doge";

    /// <summary>
    /// Require every authorised worker to provide a valid auxiliary payout address.
    /// </summary>
    public bool RequireAuxAddress { get; set; } = true;
}
