using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

public class BitcoinBlockTransaction
{
    /// <summary>
    /// transaction data encoded in hexadecimal (byte-for-byte)
    /// </summary>
    public string Data { get; set; }

    /// <summary>
    /// transaction id encoded in little-endian hexadecimal
    /// </summary>
    public string TxId { get; set; }

    /// <summary>
    /// hash encoded in little-endian hexadecimal (including witness data)
    /// </summary>
    public string Hash { get; set; }

    /// <summary>
    /// The amount of the fee in BTC
    /// </summary>
    public decimal Fee { get; set; }

    /// <summary>
    /// Transaction weight as calculated by the daemon
    /// </summary>
    public long? Weight { get; set; }
}

public class CoinbaseAux
{
    public string Flags { get; set; }
}

public class BlockTemplate
{
    [JsonIgnore]
    // The job manager fills this before publishing the shared template. Direct
    // worker jobs only read the immutable cached value, so broadcast tasks do
    // not race to parse or mutate transaction data.
    internal long ValidatedTransactionWeight { get; set; } = -1;

    /// <summary>
    /// The preferred block version
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// The hash of current highest block
    /// </summary>
    public string PreviousBlockhash { get; set; }

    /// <summary>
    /// Maximum allowable input to coinbase transaction, including the generation award and transaction fees (in Satoshis)
    /// </summary>
    public long CoinbaseValue { get; set; }

    /// <summary>
    /// The hash target
    /// </summary>
    public string Target { get; set; }

    /// <summary>
    /// A range of valid nonces
    /// </summary>
    public string NonceRange { get; set; }

    /// <summary>
    /// Current timestamp in seconds since epoch (Jan 1 1970 GMT)
    /// </summary>
    public uint CurTime { get; set; }

    /// <summary>
    /// Compressed target of next block
    /// </summary>
    public string Bits { get; set; }

    /// <summary>
    /// The height of the next block
    /// </summary>
    public uint Height { get; set; }

    /// <summary>
    /// DigiByte Odocrypt key derived by the daemon for this template.
    /// </summary>
    [JsonProperty("odokey", NullValueHandling = NullValueHandling.Ignore)]
    public uint? OdoKey { get; set; }

    /// <summary>
    /// Contents of non-coinbase transactions that should be included in the next block
    /// </summary>
    public BitcoinBlockTransaction[] Transactions { get; set; }

    /// <summary>
    /// Data that should be included in the coinbase's scriptSig content
    /// </summary>
    public CoinbaseAux CoinbaseAux { get; set; }

    /// <summary>
    /// SegWit
    /// </summary>
    [JsonProperty("default_witness_commitment")]
    public string DefaultWitnessCommitment { get; set; }

    /// <summary>
    /// CommunityAutonomous
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string CommunityAutonomousAddress { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public long CommunityAutonomousValue { get; set; }

    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}
