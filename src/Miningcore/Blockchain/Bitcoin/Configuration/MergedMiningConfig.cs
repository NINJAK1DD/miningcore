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
    /// When disabled, workers without an auxiliary address still mine the parent chain,
    /// but auxiliary block candidates are not submitted because they cannot be credited.
    /// </summary>
    public bool RequireAuxAddress { get; set; } = true;

    /// <summary>
    /// Timeout in milliseconds for recurring auxiliary template refreshes after startup.
    /// The startup sync check still uses a longer timeout so a healthy but slow Dogecoin
    /// daemon can seed the first combined job.
    /// </summary>
    public int AuxiliaryTemplatePollTimeoutMs { get; set; } = 500;

    /// <summary>
    /// Explicitly acknowledges that the current in-process recorder handoff and ZeroMQ
    /// PUB/SUB share relay are asynchronous and unacknowledged. A process failure or a
    /// disconnected relay receiver can therefore lose an accepted block event.
    /// </summary>
    public bool AcceptNonDurableBlockDelivery { get; set; }
}

/// <summary>
/// Projection used to read the merged-mining section from a pool's extension data
/// without changing the existing Bitcoin pool configuration contract.
/// </summary>
public class MergedMiningPoolConfigExtra
{
    public MergedMiningConfig MergedMining { get; set; }
}
