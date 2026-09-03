using Miningcore.Configuration;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.Configuration;

public class BitcoinPoolConfigExtra
{
    internal const bool Bip54CoinbaseDefault = true;

    /// <summary>
    /// Minimum confirmations required before a mined block is credited.
    /// </summary>
    public int? MinimumConfirmations { get; set; }

    public BitcoinAddressType AddressType { get; set; } = BitcoinAddressType.Legacy;

    public string BechPrefix { get; set; } = "bc";

    /// <summary>
    /// Maximum number of tracked jobs.
    /// Default: 12 - you should increase this value if your blockrefreshinterval is higher than 300ms
    /// </summary>
    public int? MaxActiveJobs { get; set; }

    /// <summary>
    /// Set to true to limit RPC commands to old Bitcoin command set
    /// </summary>
    public bool? HasLegacyDaemon { get; set; }

    /// <summary>
    /// Set to true to fall back to multiple sendtoaddress RPC calls for payments
    /// </summary>
    public bool HasBrokenSendMany { get; set; } = false;

    /// <summary>
    /// Allow an isolated Bitcoin-family regtest daemon to start without peers.
    /// Ignored unless getblockchaininfo reports the regtest chain.
    /// </summary>
    public bool AllowPeerlessRegtest { get; set; } = false;

    /// <summary>
    /// Arbitrary string appended at end of coinbase tx
    /// Overrides property of same name from BitcoinTemplate
    /// </summary>
    public string CoinbaseTxComment { get; set; }

    /// <summary>
    /// Blocktemplate stream published via ZMQ
    /// </summary>
    public ZmqPubSubEndpointConfig BtStream { get; set; }

    /// <summary>
    /// Custom Arguments for getblocktemplate RPC
    /// </summary>
    public JToken GBTArgs { get; set; }

    /// <summary>
    /// Pay canonical Bitcoin SOLO rewards directly in the accepted block's
    /// coinbase transaction. Disabled by default for compatibility.
    /// </summary>
    public bool SoloCoinbasePayout { get; set; } = false;

    /// <summary>
    /// Emit the BIP 54-forward-compatible locktime/sequence fields and
    /// value-first witness-output layout for the canonical Bitcoin template.
    /// Enabled by default. Set to false only as a temporary compatibility
    /// fallback for an incompatible miner or proxy.
    /// </summary>
    public bool Bip54Coinbase { get; set; } = Bip54CoinbaseDefault;

    internal static bool ResolveBip54Coinbase(
        BitcoinPoolConfigExtra config) => config?.Bip54Coinbase ??
        Bip54CoinbaseDefault;
}
