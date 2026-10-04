using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Newtonsoft.Json;

namespace Miningcore.Blockchain.BitcoinBlake2b;

// Opt-in response types: another Bitcoin-family daemon's optional extension
// cannot invoke the strict expected-work converter on its shared RPC path.
public class BitcoinBlake2bMiningInfo : MiningInfo
{
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
    public BitcoinBlake2bMiningInfoNext Next { get; set; }
}

public class BitcoinBlake2bMiningInfoNext
{
    public uint Height { get; set; }
    public string Bits { get; set; }
    public string Target { get; set; }
    public double? Difficulty { get; set; }
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}

public class BitcoinBlake2bRpcBlock : Block
{
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}

public class BitcoinBlake2bBlockchainInfo : BlockchainInfo
{
    [JsonProperty("difficulty_blake2b")]
    public Blake2bHashWork Blake2bExpectedHashWork { get; set; }
}

public sealed record BitcoinBlake2bBlockHeader(string Hash, uint Height, int Confirmations);
