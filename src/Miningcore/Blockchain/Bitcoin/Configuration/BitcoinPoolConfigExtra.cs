using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.Configuration;

internal enum BitcoinDirectSoloPayoutMode
{
    Disabled,
    Implicit,
    Explicit,
}

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
    /// coinbase transaction. Enabled by default for canonical Bitcoin SOLO
    /// pools. Set to false to retain custodial settlement.
    /// </summary>
    public bool? SoloCoinbasePayout { get; set; }

    /// <summary>
    /// Emit the BIP 54-forward-compatible locktime/sequence fields and
    /// value-first witness-output layout for the canonical Bitcoin template.
    /// Enabled by default. Set to false only as a temporary compatibility
    /// fallback for an incompatible miner or proxy.
    /// </summary>
    public bool? Bip54Coinbase { get; set; }

    internal static bool ResolveBip54Coinbase(PoolConfig pool,
        BitcoinPoolConfigExtra config)
    {
        if(config?.Bip54Coinbase is bool configured)
            return configured;

        if(HasRawProperty(pool, "bip54Coinbase"))
        {
            throw new PoolStartupException(
                $"Pool '{pool.Id}' could not safely bind Bitcoin pool " +
                "extension data while bip54Coinbase is present; correct " +
                "malformed or mistyped extension fields before startup",
                pool.Id);
        }

        return Bip54CoinbaseDefault;
    }

    internal static bool ResolveSoloCoinbasePayout(PoolConfig pool,
        BitcoinPoolConfigExtra config) => ResolveSoloCoinbasePayoutMode(pool,
        config) != BitcoinDirectSoloPayoutMode.Disabled;

    internal static BitcoinDirectSoloPayoutMode
        ResolveSoloCoinbasePayoutMode(PoolConfig pool,
            BitcoinPoolConfigExtra config)
    {
        if(config?.SoloCoinbasePayout is bool configured)
        {
            return configured ? BitcoinDirectSoloPayoutMode.Explicit :
                BitcoinDirectSoloPayoutMode.Disabled;
        }

        // The raw loader owns canonical casing and Boolean-token validation.
        // Seeing the key without a bound nullable value means extension binding
        // failed; never reinterpret an explicit operator choice as the default.
        if(HasRawProperty(pool, "soloCoinbasePayout"))
        {
            throw new PoolStartupException(
                $"Pool '{pool.Id}' could not safely bind Bitcoin pool " +
                "extension data while soloCoinbasePayout is present; " +
                "correct malformed or mistyped extension fields before startup",
                pool.Id);
        }

        // Runtime template identity is not assigned during the first config
        // pass. ValidateBitcoinDirectSoloDeployment owns the stricter family,
        // symbol and canonical-name check before listeners are reserved.
        return string.Equals(pool?.Coin, "bitcoin",
                   StringComparison.Ordinal) &&
               pool.PaymentProcessing?.PayoutScheme == PayoutScheme.SOLO
            ? BitcoinDirectSoloPayoutMode.Implicit
            : BitcoinDirectSoloPayoutMode.Disabled;
    }

    private static bool HasRawProperty(PoolConfig pool, string name) =>
        pool?.Extra?.Keys.Any(key => string.Equals(key, name,
            StringComparison.OrdinalIgnoreCase)) == true;
}
